use std::time::{SystemTime, UNIX_EPOCH};

fn main() {
    // 注入编译时间戳，用于窗口标题显示，便于确认当前运行的 exe 是否为最新构建
    // （详情面板调宽 bug 反复出现时，常因旧实例未真正关闭导致）。
    let t = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    println!("cargo:rustc-env=BUILD_TS={t}");
}
