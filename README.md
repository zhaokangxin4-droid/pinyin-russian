# 拼音俄语助手 / Pinyin Russian

Windows 微软拼音输入法的俄语翻译伴侣。输入拼音后，在候选框旁显示**当前选中词或句子**的俄语，并支持快捷键直接输入俄语。

![选中候选项的浮窗示例](docs/selected-candidate.png)

上图为程序的模拟候选项排版验证，展示第 2 项“谢谢”的译文与重音。

## 下载与启动

1. 在 [Releases](https://github.com/zhaokangxin4-droid/pinyin-russian/releases) 下载 `PinyinRussian-v1.0.0-win-x64.zip`，完整解压。
2. 在 Windows 64 位系统安装并运行 [Ollama](https://ollama.com/download/windows)，下载所用模型：

   ```powershell
   ollama pull translategemma:4b
   ```

3. 打开 Windows 设置 → 时间和语言 → 语言和区域 → 微软拼音 → 常规 → 兼容性，开启“使用以前版本的微软拼音输入法”。如果刚修改，重新打开要输入文字的软件。
4. 双击解压目录中的 `PinyinRussian.exe`，在中文模式下输入拼音，稍停后查看俄语浮窗。

`PinyinRussian.exe`、`Interop.UIAutomationClient.dll` 和 `data` 文件夹必须保存在一起。程序使用 Windows 的 .NET Framework，不需要 Python，也不需要 API 密钥。模型需要单独下载，安装包不含模型权重。

## 功能

- 只翻译当前选中的候选词或句子；切换选中项时自动更新，不批量翻译整页。
- RUAccent 本地词典为俄语标注重音，如 `Спаси́бо`、`Здра́вствуйте`。
- `Ctrl + Alt + R`：取消尚未上屏的拼音，把当前选中项的俄语写入输入框。不会按回车或发送消息。
- `Ctrl + Alt + C`：复制当前选中项的俄语。
- `Ctrl + Alt + P`：暂停或继续自动翻译。
- 主窗口支持手动整句翻译；关闭窗口后在系统托盘继续运行。

同形异音词、е/ё 歧义词和未收录的多音节词不猜测重音，显示 `〔?〕`。这个提示仅用于显示，不会写入或复制到目标输入框。翻译准确性依赖上下文，单字、多义词和语气词可能不准确。

更多操作与限制见 [使用说明](使用说明.md)。

## 本地运行与数据

翻译固定使用本机 Ollama 的 [`translategemma:4b`](https://ollama.com/library/translategemma:4b)，请求只发往 `http://127.0.0.1:11434/api/chat`。助手不会把输入文字写入日志，翻译缓存保存在内存中。首次使用需要联网安装依赖和下载模型；下载后翻译在本地进行。

本项目是 Windows 桌面程序。GitHub 提供源码与安装包下载，网页本身不接管输入法。

## 从源码编译

仓库包含 C# 源码、编译脚本和 UI Automation 的 COM 互操作程序集生成器。重音词典的二进制文件超过普通 Git 单文件大小限制，因此随 Release 安装包分发，不放进 Git 历史。

将 Release 安装包内的 `data/russian-stress.bin` 与 `data/russian-stress-ambiguous.txt` 复制到本仓库的 `data` 文件夹，再执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

编译脚本使用 Windows 自带的 .NET Framework 64 位 C# 编译器，并在缺少 COM 互操作程序集时从系统的 `UIAutomationCore.dll` 自动生成。输出为根目录下的 `PinyinRussian.exe` 和 `Interop.UIAutomationClient.dll`；生成器源码保存在 `tools/ImportUIAutomation.cs`。

也可以按固定的上游版本自行生成词典，见 [词典构建说明](scripts/README.md)。

## 验证与兼容性

微软拼音候选框识别、浮窗显示及俄语输入快捷键曾在 Codex 的真实键盘输入中确认。当前选中项模式另外检查了单项请求、选中项切换、旧响应丢弃、候选框关闭、缓存复用、重音保留和屏幕边界。

不同软件的输入控件可能有兼容性差异；以管理员权限运行的目标软件可能拒绝普通权限助手的输入。快捷键被其他软件占用时，主窗口会提示。

## 第三方来源

重音词典数据的固定版本、处理方式与 MIT 许可见 [data/来源.md](data/来源.md) 和 [data/RUAccent-LICENSE.txt](data/RUAccent-LICENSE.txt)。Ollama 与 TranslateGemma 由用户自行安装，各自遵循其上游许可；仓库和安装包不包含模型权重。
