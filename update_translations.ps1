# 游戏更新后运行此脚本，从 SQLite 缓存重新导出 translations_zh_cn.json
# 需要 .NET Framework（游戏自带）

$cacheDir = "$env:LOCALAPPDATA\..\LocalLow\Tempo Storm\The Bazaar\prod\cache"
$gameManaged = "F:\SteamLibrary\steamapps\common\The Bazaar\TheBazaar_Data\Managed"

# 加载 Mono.Data.Sqlite
[System.Reflection.Assembly]::LoadFrom("$gameManaged\Mono.Data.Sqlite.dll") | Out-Null

$dbPath = "$cacheDir\translations\zh-CN.bytes"
$connStr = "URI=file:$dbPath"
$conn = New-Object Mono.Data.Sqlite.SqliteConnection($connStr)
$conn.Open()

# 查看表结构
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'"
$reader = $cmd.ExecuteReader()
Write-Host "=== 数据库表 ==="
while ($reader.Read()) { Write-Host "  $($reader[0])" }
$reader.Close()

# 看第一张表的结构和内容
$cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' LIMIT 1"
$tableName = $cmd.ExecuteScalar()
Write-Host "`n=== 表 $tableName 结构 ==="
$cmd.CommandText = "PRAGMA table_info($tableName)"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) { Write-Host "  $($reader[1]) ($($reader[2]))" }
$reader.Close()

# 导出翻译
Write-Host "`n=== 导出翻译 ==="
$cmd.CommandText = "SELECT * FROM $tableName LIMIT 3"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    for ($i=0; $i -lt $reader.FieldCount; $i++) {
        $val = $reader[$i]
        if ($val -is [string] -and $val.Length -gt 200) { $val = $val.Substring(0, 200) + "..." }
        Write-Host "  [$i] $($reader.GetName($i)) = $val"
    }
    Write-Host "  ---"
}
$reader.Close()
$conn.Close()

Write-Host "`n完成！根据上面的列名修改脚本中的 SQL 查询即可。"
