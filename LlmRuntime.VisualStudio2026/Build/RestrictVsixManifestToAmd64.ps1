param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$InstallationTargetVersion = "[18.0,19.0)",

    [string]$PrerequisiteVersion = "[18.0,19.0)"
)

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "VSIX manifest not found: $ManifestPath"
}

[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaceManager.AddNamespace("vs", "http://schemas.microsoft.com/developer/vsx-schema/2011")

$targets = @($manifest.SelectNodes("//vs:InstallationTarget", $namespaceManager))
foreach ($target in $targets) {
    $architecture = $target.SelectSingleNode("vs:ProductArchitecture", $namespaceManager)
    if ($architecture -and [string]::Equals($architecture.InnerText.Trim(), "arm64", [System.StringComparison]::OrdinalIgnoreCase)) {
        [void]$target.ParentNode.RemoveChild($target)
        continue
    }

    [void]$target.SetAttribute("Version", $InstallationTargetVersion)
    if (-not $architecture) {
        $architecture = $manifest.CreateElement("ProductArchitecture", "http://schemas.microsoft.com/developer/vsx-schema/2011")
        [void]$target.AppendChild($architecture)
    }
    $architecture.InnerText = "amd64"
}

$installation = $manifest.SelectSingleNode("//vs:Installation", $namespaceManager)
if ($null -ne $installation) {
    $existingTargets = @($installation.SelectNodes("vs:InstallationTarget", $namespaceManager))
    $targetIds = @(
        "Microsoft.VisualStudio.Community",
        "Microsoft.VisualStudio.Pro",
        "Microsoft.VisualStudio.Enterprise"
    )

    foreach ($targetId in $targetIds) {
        $found = $false
        foreach ($target in $existingTargets) {
            if ([string]::Equals($target.GetAttribute("Id"), $targetId, [System.StringComparison]::OrdinalIgnoreCase)) {
                $found = $true
                break
            }
        }

        if ($found) {
            continue
        }

        $target = $manifest.CreateElement("InstallationTarget", "http://schemas.microsoft.com/developer/vsx-schema/2011")
        [void]$target.SetAttribute("Id", $targetId)
        [void]$target.SetAttribute("Version", $InstallationTargetVersion)
        $architecture = $manifest.CreateElement("ProductArchitecture", "http://schemas.microsoft.com/developer/vsx-schema/2011")
        $architecture.InnerText = "amd64"
        [void]$target.AppendChild($architecture)
        [void]$installation.AppendChild($target)
    }
}

$prerequisites = @($manifest.SelectNodes("//vs:Prerequisite", $namespaceManager))
foreach ($prerequisite in $prerequisites) {
    if ([string]::Equals($prerequisite.GetAttribute("Id"), "Microsoft.VisualStudio.Component.CoreEditor", [System.StringComparison]::OrdinalIgnoreCase)) {
        [void]$prerequisite.SetAttribute("Version", $PrerequisiteVersion)
    }
}

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)

$writer = [System.Xml.XmlWriter]::Create($ManifestPath, $settings)
try {
    $manifest.Save($writer)
}
finally {
    $writer.Dispose()
}
