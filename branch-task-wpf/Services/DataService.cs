using System;
using System.IO;
using System.Text.Json;
using BranchTaskWpf.Converters;
using BranchTaskWpf.Models;

namespace BranchTaskWpf.Services;

/// <summary>
/// 数据持久化服务，与 Rust 版读写相同路径 ~/.branch-task/projects.json
/// </summary>
public class DataService
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".branch-task");

    private static readonly string DataPath = Path.Combine(DataDir, "projects.json");
    private static readonly string UiPath = Path.Combine(DataDir, "wpf_ui.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        // 关键：大小写不敏感。Rust 版/旧版用 PascalCase，本版用 [JsonPropertyName] 小写，
        // 跨版本或手工改文件时若大小写不一致，反序列化会整体读空 → 触发示例项目覆盖用户数据。
        // 开此开关后无论 title/Title 都能正确绑定，彻底避免"软件一变数据就没了"。
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonElementArrayConverter() }
    };

    /// <summary>中间结果工作目录: ~/.branch-task/workspaces/{pid}/{tid}/</summary>
    public static string GetWorkspaceDir(string projectId, string taskId)
    {
        var dir = Path.Combine(DataDir, "workspaces", projectId, taskId);
        Directory.CreateDirectory(dir);
        return dir + "\\";
    }

    /// <summary>拷贝文件到工作目录（同名加时间戳区分）</summary>
    public static string CopyToWorkspace(string srcPath, string wsDir)
    {
        var name = Path.GetFileName(srcPath);
        var dest = Path.Combine(wsDir, name);
        if (File.Exists(dest))
        {
            var ts = DateTime.Now.ToString("HHmmss");
            dest = Path.Combine(wsDir,
                Path.GetFileNameWithoutExtension(name) + "_" + ts + Path.GetExtension(name));
        }
        File.Copy(srcPath, dest, overwrite: false);
        return dest;
    }

    public static Store Load()
    {
        try
        {
            if (File.Exists(DataPath))
            {
                var json = File.ReadAllText(DataPath);
                var store = JsonSerializer.Deserialize<Store>(json, JsonOpts);
                // [diag] 文件日志：记录每次 Load 读到的项目数
                try { var cnt = (store == null) ? "null" : $"{store.Projects.Count}"; File.AppendAllText(DataPath + ".load_diag.log",
                    $"[{DateTime.Now:HH:mm:ss.fff}] Load: json_len={json.Length} store={cnt} proj\n"); } catch {}
                if (store != null && store.Projects.Count > 0)
                    return store;
                // 文件存在但反序列化失败或空 → 保留原文件，返回空 Store 但不保存
                System.Diagnostics.Debug.WriteLine("DataService: projects.json exists but yielded empty projects");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load failed: {ex.Message}");
            // 加载失败 → 备份损坏文件，不覆盖
            try
            {
                var backup = DataPath + ".backup." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(DataPath, backup);
                System.Diagnostics.Debug.WriteLine($"Backed up corrupt file to {backup}");
            }
            catch { }
        }
        return new Store();
    }

    public static void Save(Store store)
    {
        // 独立日志路径，避免和 save_diag 混在一起
        var errLog = Path.Combine(DataDir, "save_error.log");
        try
        {
            // [diag] 文件日志：记录每次 Save 写入的项目数（写盘前记录，仅作时间线参考）
            try { File.AppendAllText(DataPath + ".save_diag.log",
                $"[{DateTime.Now:HH:mm:ss.fff}] Save: {store.Projects.Count} proj, names=[{string.Join(", ", store.Projects.Select(p => p.Name))}]\n"); } catch {}
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(store, JsonOpts);

            // 唯一临时文件(btwpf89)：多实例并发时固定 .tmp 会互相覆盖/锁冲突，用 GUID 避免
            var tmp = DataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, DataPath, overwrite: true);

            // 写盘后强制回读校验(btwpf89)：项目数不一致 → 重试最多 3 次，仍失败则留现场
            bool ok = false;
            for (int attempt = 0; attempt < 3 && !ok; attempt++)
            {
                try
                {
                    var back = Load();
                    if (back != null && back.Projects.Count == store.Projects.Count)
                        ok = true;
                }
                catch { }
                if (!ok)
                {
                    try { File.AppendAllText(errLog, $"[{DateTime.Now:HH:mm:ss.fff}] VERIFY-RETRY {attempt}: wrote {store.Projects.Count} read {Load()?.Projects.Count}\n"); } catch { }
                    System.Threading.Thread.Sleep(100);
                }
            }
            if (!ok)
            {
                var msg = $"[{DateTime.Now:HH:mm:ss.fff}] VERIFY-FAIL: wrote {store.Projects.Count} but read back different\n";
                try { File.AppendAllText(errLog, msg); } catch { }
                try { File.Copy(DataPath, DataPath + ".verify_fail_" + DateTime.Now.ToString("HHmmss") + ".json", overwrite: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            // 关键修复：写盘失败必须落盘记录（之前只 Debug.WriteLine，静默丢失数据）
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(errLog, $"[{DateTime.Now:HH:mm:ss.fff}] SAVE-FAILED: {ex}\n");
                // 失败时把内存数据另存为 .recover 文件，至少不丢
                try
                {
                    var json = JsonSerializer.Serialize(store, JsonOpts);
                    File.WriteAllText(DataPath + ".recover_" + DateTime.Now.ToString("HHmmss") + ".json", json);
                }
                catch { }
            }
            catch { }
            System.Diagnostics.Debug.WriteLine($"Save failed: {ex.Message}");
        }
    }

    /// <summary>加载 UI 状态（当前项目/面板展开/层级配色），文件不存在返回默认值</summary>
    public static UiState LoadUiState()
    {
        try
        {
            if (File.Exists(UiPath))
            {
                var json = File.ReadAllText(UiPath);
                var ui = JsonSerializer.Deserialize<UiState>(json, JsonOpts);
                if (ui != null) return ui;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadUiState failed: {ex.Message}");
        }
        return new UiState();
    }

    /// <summary>保存 UI 状态</summary>
    public static void SaveUiState(UiState ui)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = UiPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ui, JsonOpts));
            File.Move(tmp, UiPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveUiState failed: {ex.Message}");
        }
    }
}
