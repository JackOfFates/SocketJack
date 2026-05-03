<#
.SYNOPSIS
    SocketJack DbContext Generator - Generates Entity Framework DbContext from SQL Server schema

.PARAMETER ConnectionString
    SQL Server connection string

.PARAMETER Namespace
    Namespace for generated classes (default: Generated)

.PARAMETER ContextName
    Name of the DbContext class (default: AppDbContext)

.PARAMETER OutputDirectory
    Output directory for generated files (default: current directory)

.PARAMETER Language
    Output language: cs or vb (default: cs)

.PARAMETER Partial
    Generate partial classes (default: true)

.PARAMETER Exclude
    Comma-separated list of tables to exclude
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$ConnectionString,

    [string]$Namespace = "Generated",
    [string]$ContextName = "AppDbContext",
    [string]$OutputDirectory = ".",
    [string]$Language = "cs",
    [string]$Partial = "true",
    [string]$Exclude = "__EFMigrationsHistory,sysdiagrams"
)

$ErrorActionPreference = 'Stop'

$isVb = $Language.ToLower() -eq 'vb'
$generatePartial = $Partial -ne 'false'
$excluded = @($Exclude.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })

Write-Host "DbContext Generator: Connecting to database..."

$conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
$conn.Open()

# Get tables
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE'"
$reader = $cmd.ExecuteReader()
$tables = @()
while ($reader.Read()) {
    $name = $reader.GetString(0)
    if ($name -notin $excluded) { $tables += $name }
}
$reader.Close()

Write-Host "DbContext Generator: Found $($tables.Count) tables"

function ToPascalCase($name) {
    $result = ""
    $cap = $true
    foreach ($c in $name.ToCharArray()) {
        if ($c -eq '_' -or $c -eq ' ') { $cap = $true; continue }
        $result += if ($cap) { [char]::ToUpper($c) } else { $c }
        $cap = $false
    }
    return $result
}

function Singularize($name) {
    # Handle common plural endings
    if ($name.EndsWith("ies") -and $name.Length -gt 3) {
        return $name.Substring(0, $name.Length - 3) + "y"
    }
    if ($name.EndsWith("ses") -or $name.EndsWith("xes") -or $name.EndsWith("ches") -or $name.EndsWith("shes")) {
        return $name.Substring(0, $name.Length - 2)
    }
    if ($name.EndsWith("s") -and -not $name.EndsWith("ss") -and $name.Length -gt 1) {
        return $name.Substring(0, $name.Length - 1)
    }
    return $name
}

function Pluralize($name) {
    if ($name.EndsWith("y") -and $name.Length -gt 1 -and "aeiou" -notmatch $name[-2]) {
        return $name.Substring(0, $name.Length-1) + "ies"
    }
    if ($name.EndsWith("s") -or $name.EndsWith("x") -or $name.EndsWith("ch") -or $name.EndsWith("sh")) {
        return $name + "es"
    }
    return $name + "s"
}

function SqlToCsType($sql) {
    switch ($sql.ToLower()) {
        "bigint" { return "long" }
        "bit" { return "bool" }
        "int" { return "int" }
        "smallint" { return "short" }
        "tinyint" { return "byte" }
        { $_ -in "decimal","numeric","money","smallmoney" } { return "decimal" }
        "float" { return "double" }
        "real" { return "float" }
        { $_ -in "datetime","datetime2","date","smalldatetime" } { return "DateTime" }
        "uniqueidentifier" { return "Guid" }
        { $_ -in "varbinary","binary","image","timestamp" } { return "byte[]" }
        default { return "string" }
    }
}

function SqlToVbType($sql) {
    switch ($sql.ToLower()) {
        "bigint" { return "Long" }
        "bit" { return "Boolean" }
        "int" { return "Integer" }
        "smallint" { return "Short" }
        "tinyint" { return "Byte" }
        { $_ -in "decimal","numeric","money","smallmoney" } { return "Decimal" }
        "float" { return "Double" }
        "real" { return "Single" }
        { $_ -in "datetime","datetime2","date","smalldatetime" } { return "Date" }
        "uniqueidentifier" { return "Guid" }
        { $_ -in "varbinary","binary","image","timestamp" } { return "Byte()" }
        default { return "String" }
    }
}

function IsValueType($sql) {
    $t = $sql.ToLower()
    # These are reference types (strings, arrays) that don't need nullable suffix
    return $t -notin "varchar","nvarchar","char","nchar","text","ntext","xml","varbinary","binary","image","timestamp"
}

function EscapeVbKeyword($name) {
    $vbKeywords = @(
        "AddHandler","AddressOf","Alias","And","AndAlso","As","Boolean","ByRef","Byte","ByVal",
        "Call","Case","Catch","CBool","CByte","CChar","CDate","CDbl","CDec","Char","CInt","Class",
        "CLng","CObj","Const","Continue","CSByte","CShort","CSng","CStr","CType","CUInt","CULng",
        "CUShort","Date","Decimal","Declare","Default","Delegate","Dim","DirectCast","Do","Double",
        "Each","Else","ElseIf","End","EndIf","Enum","Erase","Error","Event","Exit","False","Finally",
        "For","Friend","Function","Get","GetType","Global","GoSub","GoTo","Handles","If","Implements",
        "Imports","In","Inherits","Integer","Interface","Is","IsNot","Let","Lib","Like","Long","Loop",
        "Me","Mod","Module","MustInherit","MustOverride","MyBase","MyClass","Namespace","Narrowing",
        "New","Next","Not","Nothing","NotInheritable","NotOverridable","Object","Of","On","Operator",
        "Option","Optional","Or","OrElse","Overloads","Overridable","Overrides","ParamArray","Partial",
        "Private","Property","Protected","Public","RaiseEvent","ReadOnly","ReDim","REM","RemoveHandler",
        "Resume","Return","SByte","Select","Set","Shadows","Shared","Short","Single","Static","Step",
        "Stop","String","Structure","Sub","SyncLock","Then","Throw","To","True","Try","TryCast","TypeOf",
        "UInteger","ULong","UShort","Using","Variant","Wend","When","While","Widening","With","WithEvents",
        "WriteOnly","Xor"
    )
    if ($vbKeywords -contains $name) {
        return "[$name]"
    }
    return $name
}

# Create output directory if needed
if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

# Generate entity classes
foreach ($table in $tables) {
    # Singularize table name for entity class (e.g., "Accounts" -> "Account")
    $className = Singularize (ToPascalCase $table)

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table"
    $cmd.Parameters.AddWithValue("@table", $table) | Out-Null
    $reader = $cmd.ExecuteReader()
    $columns = @()
    while ($reader.Read()) {
        $columns += @{
            Name = $reader.GetString(0)
            Type = $reader.GetString(1)
            Nullable = $reader.GetString(2) -eq "YES"
        }
    }
    $reader.Close()

    $sb = [System.Text.StringBuilder]::new()

    if ($isVb) {
        [void]$sb.AppendLine("Imports System")
        [void]$sb.AppendLine()
        $partialStr = if ($generatePartial) { "Partial " } else { "" }
        [void]$sb.AppendLine("${partialStr}Public Class $className")
        foreach ($col in $columns) {
            $propName = EscapeVbKeyword (ToPascalCase $col.Name)
            $vbType = SqlToVbType $col.Type
            $nullSuffix = if ($col.Nullable -and (IsValueType $col.Type)) { "?" } else { "" }
            [void]$sb.AppendLine("    Public Property $propName As $vbType$nullSuffix")
        }
        [void]$sb.AppendLine("End Class")
    } else {
        [void]$sb.AppendLine("using System;")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("namespace $Namespace {")
        $partialStr = if ($generatePartial) { "partial " } else { "" }
        [void]$sb.AppendLine("    public ${partialStr}class $className {")
        foreach ($col in $columns) {
            $propName = ToPascalCase $col.Name
            $csType = SqlToCsType $col.Type
            $nullSuffix = if ($col.Nullable -and (IsValueType $col.Type)) { "?" } else { "" }
            [void]$sb.AppendLine("        public $csType$nullSuffix $propName { get; set; }")
        }
        [void]$sb.AppendLine("    }")
        [void]$sb.AppendLine("}")
    }

    $ext = if ($isVb) { ".vb" } else { ".cs" }
    $filePath = Join-Path $OutputDirectory "$className$ext"
    [System.IO.File]::WriteAllText($filePath, $sb.ToString())
    Write-Host "  Generated: $className$ext"
}

# Generate DbContext
$ctxSb = [System.Text.StringBuilder]::new()
$escapedConnStr = $ConnectionString.Replace('"', '""')

if ($isVb) {
    [void]$ctxSb.AppendLine("Imports Microsoft.EntityFrameworkCore")
    [void]$ctxSb.AppendLine()
    $partialStr = if ($generatePartial) { "Partial " } else { "" }
    [void]$ctxSb.AppendLine("${partialStr}Public Class $ContextName")
    [void]$ctxSb.AppendLine("    Inherits DbContext")
    [void]$ctxSb.AppendLine()
    [void]$ctxSb.AppendLine("    Public Sub New()")
    [void]$ctxSb.AppendLine("    End Sub")
    [void]$ctxSb.AppendLine()
    [void]$ctxSb.AppendLine("    Public Sub New(options As DbContextOptions(Of $ContextName))")
    [void]$ctxSb.AppendLine("        MyBase.New(options)")
    [void]$ctxSb.AppendLine("    End Sub")
    [void]$ctxSb.AppendLine()
    foreach ($table in $tables) {
        # Singularize for entity class, use table name for DbSet property
        $className = Singularize (ToPascalCase $table)
        $pluralName = ToPascalCase $table  # Use original table name for DbSet property
        [void]$ctxSb.AppendLine("    Public Overridable Property $pluralName As DbSet(Of $className)")
    }
    [void]$ctxSb.AppendLine()
    [void]$ctxSb.AppendLine("    Protected Overrides Sub OnConfiguring(optionsBuilder As DbContextOptionsBuilder)")
    [void]$ctxSb.AppendLine("        If Not optionsBuilder.IsConfigured Then")
    [void]$ctxSb.AppendLine("            optionsBuilder.UseSqlServer(`"$escapedConnStr`")")
    [void]$ctxSb.AppendLine("        End If")
    [void]$ctxSb.AppendLine("    End Sub")
    [void]$ctxSb.AppendLine("End Class")
} else {
    $escapedConnStrCs = $ConnectionString.Replace('\', '\\').Replace('"', '\"')
    [void]$ctxSb.AppendLine("using System;")
    [void]$ctxSb.AppendLine("using Microsoft.EntityFrameworkCore;")
    [void]$ctxSb.AppendLine()
    [void]$ctxSb.AppendLine("namespace $Namespace {")
    $partialStr = if ($generatePartial) { "partial " } else { "" }
    [void]$ctxSb.AppendLine("    public ${partialStr}class $ContextName : DbContext {")
    [void]$ctxSb.AppendLine()
    [void]$ctxSb.AppendLine("        public $ContextName() { }")
    [void]$ctxSb.AppendLine("        public $ContextName(DbContextOptions<$ContextName> options) : base(options) { }")
    [void]$ctxSb.AppendLine()
    foreach ($table in $tables) {
        # Singularize for entity class, use table name for DbSet property
        $className = Singularize (ToPascalCase $table)
        $pluralName = ToPascalCase $table  # Use original table name for DbSet property
        [void]$ctxSb.AppendLine("        public virtual DbSet<$className> $pluralName { get; set; }")
    }
    [void]$ctxSb.AppendLine()
    [void]$ctxSb.AppendLine("        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {")
    [void]$ctxSb.AppendLine("            if (!optionsBuilder.IsConfigured) {")
    [void]$ctxSb.AppendLine("                optionsBuilder.UseSqlServer(`"$escapedConnStrCs`");")
    [void]$ctxSb.AppendLine("            }")
    [void]$ctxSb.AppendLine("        }")
    [void]$ctxSb.AppendLine("    }")
    [void]$ctxSb.AppendLine("}")
}

$ctxExt = if ($isVb) { ".vb" } else { ".cs" }
$ctxPath = Join-Path $OutputDirectory "$ContextName$ctxExt"
[System.IO.File]::WriteAllText($ctxPath, $ctxSb.ToString())
Write-Host "  Generated: $ContextName$ctxExt"

$conn.Close()
Write-Host "DbContext Generator: Generated $($tables.Count + 1) files successfully"
