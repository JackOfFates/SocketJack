param(
    [Parameter(Mandatory = $true)]
    [string]$VsixPath
)

if (-not (Test-Path -LiteralPath $VsixPath)) {
    throw "VSIX package not found: $VsixPath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($VsixPath, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.GetEntry("extension.vsixmanifest")
    if ($null -eq $entry) {
        throw "extension.vsixmanifest was not found in $VsixPath"
    }

    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        [xml]$manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $metadata = $manifest.PackageManifest.Metadata
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace("vs", "http://schemas.microsoft.com/developer/vsx-schema/2011")

    foreach ($nodeName in @("Icon", "PreviewImage", "License", "ReleaseNotes")) {
        $node = $metadata.SelectSingleNode("vs:$nodeName", $namespaceManager)
        if ($null -ne $node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            $node.InnerText = $node.InnerText.Replace("\", "/")
        }
    }

    $entry.Delete()
    $newEntry = $zip.CreateEntry("extension.vsixmanifest", [System.IO.Compression.CompressionLevel]::Optimal)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)

    $writer = [System.Xml.XmlWriter]::Create($newEntry.Open(), $settings)
    try {
        $manifest.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $zip.Dispose()
}
