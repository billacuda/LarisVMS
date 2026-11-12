# 備份

資料庫備份，以及還有哪些內容需要備份。

**設定 → 備份**會依每日排程或隨需對 LarisVMS 資料庫進行完整的 SQL Server 備份。它適用於 SQL Express，後者沒有可排程自身備份的 SQL Agent。

- **備份目錄**：*SQL Server 服務*可以寫入的資料夾，位於 LarisVMS 安裝資料夾之外。如果 SQL Server 在另一台電腦上，此路徑必須存在於那台電腦上，並且舊備份不會被自動清理。
- **保留最近**：保留多少份備份（僅限本機 SQL Server）。

還原在 LarisVMS 之外使用 SSMS 或 `sqlcmd` 完成。備份只有完整備份。在 SQL Express 上，請使用 SIMPLE 復原模式。

## MariaDB

在 MariaDB 上，備份使用 `mariadb-dump` 進行，並儲存為壓縮的 `.sql.gz` 檔案。`mariadb-dump` 在 LarisVMS 伺服器上執行，因此必須在那裡安裝 MariaDB 用戶端工具，即使 MariaDB 本身執行在另一台電腦上。LarisVMS 會在 `C:\Program Files\MariaDB …` 下找到它們，超級管理員也可以在「備份」頁面上設定 `mariadb-dump.exe` 的路徑。該路徑必須位於此伺服器上、只有系統管理員才能變更的資料夾中（例如 Program Files 下），因為 LarisVMS 會以服務自身的權限執行它。

- **備份目錄**：LarisVMS 伺服器上（或共用中）*LarisVMS 服務*可以寫入的資料夾，位於安裝資料夾之外。無論 MariaDB 在哪裡執行，舊備份一律會被清理。
- 備份在一個一致的快照中包含整個資料庫，且在不中斷錄影的情況下完成。

解壓縮 `.sql.gz`（例如用 7-Zip），然後用 `mariadb` 用戶端還原它。該檔案會在資料庫缺失時建立它，並取代其資料表，因此請先停止 LarisVMS Web 服務：

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## 另請備份

- `%ProgramData%\LarisVMS\keys` 處的**資料保護金鑰**。攝影機密碼、電子郵件密碼和節點金鑰都用它們加密。沒有這些金鑰，還原後的資料庫無法解密其中任何一項。
- 安裝資料夾中的 **`setup-generated.json`**，其中包含資料庫連線字串。它的密碼只在儲存它的電腦上有效；在新電腦上，請重新執行安裝精靈並指向還原後的資料庫。

錄影不屬於資料庫備份的一部分。請用封存磁碟區或您自己的儲存空間備份來保護它們。
