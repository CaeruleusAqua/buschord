[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Assets,[Parameter(Mandatory=$true)][string]$Destination,[string]$Runtime)
$ErrorActionPreference='Stop'
$graph=Get-Content -LiteralPath $Assets -Raw | ConvertFrom-Json -AsHashtable
$builder=[Text.StringBuilder]::new("Third-party .NET packages in this distribution`n`n")
foreach($entry in $graph.libraries.GetEnumerator() | Sort-Object Key) {
    if($entry.Value.type -ne 'package'){continue}
    $directory=$null
    foreach($folder in $graph.packageFolders.Keys){
        $candidate=Join-Path $folder $entry.Value.path
        if(Test-Path -LiteralPath $candidate){$directory=$candidate;break}
    }
    if(!$directory){throw "Package metadata is missing: $($entry.Key)"}
    $nuspec=Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' | Select-Object -First 1
    [xml]$spec=Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata=$spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    [void]$builder.AppendLine($entry.Key)
    foreach($field in 'authors','copyright','projectUrl','license','licenseUrl'){
        $node=$metadata.SelectSingleNode("*[local-name()='$field']")
        if($node){[void]$builder.AppendLine("${field}: $($node.InnerText)")}
    }
    foreach($notice in Get-ChildItem -LiteralPath $directory -File | Where-Object Name -Match '^(LICENSE|LICENCE|NOTICE|COPYING)(\..*)?$'){
        [void]$builder.AppendLine([IO.File]::ReadAllText($notice.FullName))
    }
    [void]$builder.AppendLine()
}
[void]$builder.AppendLine([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'licenses/Velopack-LICENSE.txt')))
if($graph.libraries.ContainsKey('Avalonia.Fonts.Inter/12.1.4')){
    [void]$builder.AppendLine("`nInter 3.019 font family (bundled by Avalonia.Fonts.Inter): SIL Open Font License 1.1")
    [void]$builder.AppendLine([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'licenses/Inter-OFL-1.1.txt')))
}
if($graph.libraries.ContainsKey('Avalonia/12.1.4')){
    [void]$builder.AppendLine([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'licenses/Avalonia-LICENSE.txt')))
}
if($Runtime){
    $publish=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination))
    $runtimeConfig=Get-ChildItem $publish -Filter '*.runtimeconfig.json' | Select-Object -First 1
    $runtimeVersion=(Get-Content $runtimeConfig.FullName -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
        Where-Object name -EQ Microsoft.NETCore.App | Select-Object -First 1 -ExpandProperty version
    $found=$false
    foreach($folder in $graph.packageFolders.Keys){
        $runtimePackage=Join-Path $folder "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion"
        if(!(Test-Path -LiteralPath $runtimePackage)){continue}
        foreach($name in 'LICENSE.TXT','THIRD-PARTY-NOTICES.TXT'){
            [void]$builder.AppendLine("`n.NET runtime $Runtime $runtimeVersion $name")
            [void]$builder.AppendLine([IO.File]::ReadAllText((Join-Path $runtimePackage $name)))
        }
        $found=$true;break
    }
    if(!$found){throw 'The self-contained runtime licence/notice files could not be resolved.'}
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Destination),$builder.ToString())
