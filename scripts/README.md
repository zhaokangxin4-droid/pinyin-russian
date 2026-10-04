# 重音词典构建

已编译的词典随 Release 安装包提供，正常使用无需运行此脚本。

如需从固定上游版本重建，安装 Python 3（脚本仅使用标准库），然后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\download-dictionary.ps1
python .\scripts\build_lexicon.py --source .\dictionary-source --output .\data
```

下载脚本从 Hugging Face 的 `ruaccent/accentuator` 固定版本 `b78ae5ea1e62beaf138bed1865cd8c3b0b5ca855` 下载四个词典文件，并复制仓库保留的 MIT 许可。构建脚本生成程序使用的 RST1 二进制索引、歧义词表和来源说明。

`dictionary-source` 不纳入 Git。构建过程会读入完整词典，内存需求高于助手运行时的按需映射查询。
