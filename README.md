# TodoTransparent

一个基于 WPF (.NET Framework 4.8) 的透明待办便签应用。

## 功能

- 透明毛玻璃界面，支持深浅双主题
- 待办事项增删改查
- 子任务支持
- 拖拽排序
- 截止时间提醒
- 窗口贴边自动隐藏
- 幽灵模式（鼠标移开自动半透明）
- 自定义背景图片
- 鼠标穿透模式

## 编译

```powershell
# 使用 PowerShell 编译
.\compile_new.ps1
```

需要 .NET Framework 4.8。

## 使用

运行编译生成的 `TodoTransparent_new.exe` 即可。

数据自动保存在 `%LocalAppData%\TodoTransparent\` 目录下。
