# 成品程序

`网络信息采集和端口管理工具_v1.0_SubnetDesk.exe` 为最终单文件版本，已通过实例测试。

- 大小：86.5 MB
- SHA256：`5F6A06CC9D8B8CD64C0F0C318BF109DF75672612D3B41F820C1461E1C27A42`
- 运行环境：Windows 10 1607+ / Windows 11，64 位
- 管理员权限：清单已声明 requireAdministrator，双击自动提权
- 无需安装 .NET 运行时，也无需 VC++ 运行库
- 远程桌面：内嵌 SubnetDesk 2.0.0，首次点击“远程桌面”解压到
  `%LOCALAPPDATA%\PortSecurityTool\remote\2.0.0` 后启动
- 关闭行为：远程桌面页有“关闭 SubnetDesk”按钮；PST 关闭窗口/最小化到托盘或退出时，
  也会一并结束由本工具启动的 SubnetDesk 进程

同目录的 `网络信息采集和端口管理工具_v1.0_GUI.hta` 是界面源文件，保留用于查看/改界面；
它不是可独立运行的完整版本，原生功能需通过上面的 exe 使用。
