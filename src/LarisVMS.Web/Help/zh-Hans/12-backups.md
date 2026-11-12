# 备份

数据库备份，以及还有哪些内容需要备份。

**设置 → 备份**会按每日计划或按需对 LarisVMS 数据库进行完整的 SQL Server 备份。它适用于 SQL Express，后者没有可安排自身备份的 SQL Agent。

- **备份目录**：*SQL Server 服务*可以写入的文件夹，位于 LarisVMS 安装文件夹之外。如果 SQL Server 在另一台计算机上，此路径必须存在于那台计算机上，并且旧备份不会被自动清理。
- **保留最近**：保留多少份备份（仅限本地 SQL Server）。

恢复在 LarisVMS 之外使用 SSMS 或 `sqlcmd` 完成。备份只有完整备份。在 SQL Express 上，请使用 SIMPLE 恢复模式。

## MariaDB

在 MariaDB 上，备份使用 `mariadb-dump` 进行，并保存为压缩的 `.sql.gz` 文件。`mariadb-dump` 在 LarisVMS 服务器上运行，因此必须在那里安装 MariaDB 客户端工具，即使 MariaDB 本身运行在另一台计算机上。LarisVMS 会在 `C:\Program Files\MariaDB …` 下找到它们，超级管理员也可以在“备份”页面上设置 `mariadb-dump.exe` 的路径。该路径必须位于此服务器上、只有管理员才能更改的文件夹中（例如 Program Files 下），因为 LarisVMS 会以服务自身的权限运行它。

- **备份目录**：LarisVMS 服务器上（或共享中）*LarisVMS 服务*可以写入的文件夹，位于安装文件夹之外。无论 MariaDB 在哪里运行，旧备份总会被清理。
- 备份在一个一致的快照中包含整个数据库，且在不中断录制的情况下完成。

解压 `.sql.gz`（例如用 7-Zip），然后用 `mariadb` 客户端恢复它。该文件会在数据库缺失时创建它，并替换其表，因此请先停止 LarisVMS Web 服务：

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## 另请备份

- `%ProgramData%\LarisVMS\keys` 处的**数据保护密钥**。摄像头密码、电子邮件密钥和节点密钥都用它们加密。没有这些密钥，恢复后的数据库无法解密其中任何一项。
- 安装文件夹中的 **`setup-generated.json`**，其中包含数据库连接字符串。它的密码只在保存它的计算机上有效；在新计算机上，请重新运行安装向导并指向恢复后的数据库。

录像不属于数据库备份的一部分。请用归档卷或您自己的存储备份来保护它们。
