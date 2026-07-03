## v1.0.5 (2026-07-03)

### ✨ 新增
- 设备选择改为三级联动下拉（品牌 → 子系列 → 机型）
  - 解决原搜索框方案在 `view.Refresh()` 时反向覆盖用户输入的问题
  - 137 款设备按"品牌-子系列"自动归类（OPPO 56 / OnePlus 32 / realme 46 / DIZO 3）
  - 默认值"自动检测"保留，切换品牌/子系列时子级自动联动
- 发布旧系统兼容包 `OPPO-Pods-For-Windows-Legacy.zip`
  - 解决部分用户（Win10 早期版本 KERNELBASE.dll 过旧）无法运行 .NET 10 apphost 的问题
  - 内含 `run.bat` / `launcher.vbs`（隐藏窗口启动）/ `设置开机自启.bat` / `删除开机自启.bat`

### 🔧 优化
- 设备选择交互更稳定（无搜索框文本覆盖 bug）
- 启动器路径使用 `WScript.ScriptFullName` 解析，不再依赖硬编码路径

### 📦 发布
- `OPPO-Pods-For-Windows.exe` (7.6 MB) — 框架依赖版
- `OPPO-Pods-For-Windows-NET.exe` (180 MB) — 自包含 NET 10 运行时
- `OPPO-Pods-For-Windows-Legacy.zip` (3.1 MB) — 旧系统兼容包
