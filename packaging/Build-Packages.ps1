[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Project,
    [Parameter(Mandatory=$true)][string]$Version,
    [ValidateSet('stable','preview')][string]$Channel='preview',
    [string]$Output=(Join-Path $PSScriptRoot '../artifacts'),
    [string]$DotNet='dotnet',
    [string]$Vpk='vpk',
    [string]$AppId='BusChord',
    [string]$MainExe='BusChord',
    [Parameter(Mandatory=$true)][string]$Icon,
    [Parameter(Mandatory=$true)][string]$WindowsIcon,
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent),
    [string]$Commit='local',
    [string]$UpdateTrustSource,
    [ValidateSet('win-x64','linux-x64')][string[]]$Runtimes=@('win-x64','linux-x64'),
    [switch]$Production
)
$ErrorActionPreference='Stop'
function Remove-PublishedDebugSymbols([string]$Directory,[string]$OutputRoot) {
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    $directoryPath=[IO.Path]::GetFullPath($Directory)
    $outputPath=[IO.Path]::GetFullPath($OutputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $publishPrefix=[IO.Path]::GetFullPath((Join-Path $outputPath 'publish'))+[IO.Path]::DirectorySeparatorChar
    if(!$directoryPath.StartsWith($publishPrefix,$comparison)){throw 'Symbol cleanup requires a generated publish directory inside the package output.'}
    $parent=Get-Item -LiteralPath $directoryPath -Force
    while($null -ne $parent){
        if($parent -isnot [IO.DirectoryInfo] -or ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Symbol cleanup refuses linked output directories.'}
        if($parent.FullName.Equals($outputPath,$comparison)){break}
        $parent=$parent.Parent
    }
    if($null -eq $parent){throw 'Symbol cleanup escaped the package output.'}
    $items=@(Get-ChildItem -LiteralPath $directoryPath -Force -Recurse)
    if($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }){throw 'Symbol cleanup refuses linked publish entries.'}
    $prefix=$directoryPath.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    foreach($symbol in $items | Where-Object { !$_.PSIsContainer -and $_.Extension -eq '.pdb' }){
        if(!$symbol.FullName.StartsWith($prefix,$comparison)){throw 'Symbol cleanup escaped the publish directory.'}
        Remove-Item -LiteralPath $symbol.FullName -Force
    }
}
$destination=[IO.Path]::GetFullPath($Output)
$source=[IO.Path]::GetFullPath($SourceRoot)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
if ($Production -and (!$env:BUSCHORD_UPDATE_PUBLIC_KEY_ID -or !$env:BUSCHORD_UPDATE_PUBLIC_KEY_PEM -or !$env:BUSCHORD_UPDATE_FEED_BASE)) {
    throw 'Production builds require reviewed embedded public trust configuration.'
}
$trustProps=@()
if($UpdateTrustSource){
    $provided=[IO.Path]::GetFullPath($UpdateTrustSource)
    if(!(Test-Path -LiteralPath $provided)){throw 'The reviewed public trust source is missing.'}
    $trustProps=@("-p:BusChordUpdateTrustSource=$provided")
}
if ($Production) {
    $publicFile=Join-Path $destination 'public-update-key.pem'
    [IO.File]::WriteAllText($publicFile,$env:BUSCHORD_UPDATE_PUBLIC_KEY_PEM)
    $generated=Join-Path $destination 'ProductionUpdateTrust.g.cs'
    & $DotNet run --project (Join-Path $SourceRoot 'tools-src/BusChord.Release') --artifacts-path (Join-Path $destination 'build/trust-generator') -- `
        trust-source $env:BUSCHORD_UPDATE_FEED_BASE $env:BUSCHORD_UPDATE_PUBLIC_KEY_ID $publicFile $generated
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $generated)) { throw 'Public trust source generation failed.' }
    $trustProps=@("-p:BusChordUpdateTrustSource=$generated")
}
foreach ($rid in $Runtimes) {
    $publish=Join-Path $destination "publish/$rid"
    $build=Join-Path $destination "build/$rid"
    & $DotNet publish $Project -c Release -r $rid --self-contained true --artifacts-path $build -o $publish --nologo `
        -p:Version=$Version -p:ContinuousIntegrationBuild=true -p:Deterministic=true '-p:DebugType=None' '-p:DebugSymbols=false' `
        "-p:PathMap=$source=/_/" "-p:SourceRevisionId=$Commit" '-p:PublishTrimmed=false' '-p:PublishSingleFile=false' `
        '-p:PublishRepositoryUrl=true' '-p:EnableSourceLink=true' '-p:EmbedUntrackedSources=false' @trustProps
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $rid" }
    $workerProject=Join-Path $SourceRoot 'src/Knx.Scripting/Knx.Scripting.csproj'
    $workerProbe=Join-Path $destination "worker-probe/$rid"
    if (Test-Path $workerProject) {
        & $DotNet publish $workerProject -c Release -r $rid --self-contained true --artifacts-path (Join-Path $build 'script-worker') `
            -o (Join-Path $publish 'ScriptWorker') --nologo -p:Version=$Version -p:ContinuousIntegrationBuild=true -p:Deterministic=true `
            '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishTrimmed=false' '-p:PublishSingleFile=false' `
            "-p:PathMap=$source=/_/" "-p:SourceRevisionId=$Commit"
        if ($LASTEXITCODE -ne 0) { throw "Script worker publish failed: $rid" }
        & $DotNet publish (Join-Path $SourceRoot 'tools-src/BusChord.ScriptWorkerProbe/BusChord.ScriptWorkerProbe.csproj') `
            -c Release -r $rid --self-contained true --artifacts-path (Join-Path $build 'script-worker-probe') -o $workerProbe --nologo `
            -p:Version=$Version -p:ContinuousIntegrationBuild=true -p:Deterministic=true '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$source=/_/"
        if ($LASTEXITCODE -ne 0) { throw "Script worker package probe publish failed: $rid" }
    }
    $brokerProject=Join-Path $SourceRoot 'src/BusChord.UpdateBroker/BusChord.UpdateBroker.csproj'
    if (Test-Path $brokerProject) {
        & $DotNet publish $brokerProject -c Release -r $rid --self-contained true --artifacts-path (Join-Path $build 'broker') `
            -o (Join-Path $publish 'UpdateBroker') --nologo -p:Version=$Version -p:ContinuousIntegrationBuild=true -p:Deterministic=true `
            '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$source=/_/" @trustProps
        if ($LASTEXITCODE -ne 0) { throw "Broker publish failed: $rid" }
    }
    # Native NuGet content symbols are copied independently of DebugSymbols/DebugType.
    # Remove only PDBs from the generated, non-linked code tree before its unchanged guard.
    Remove-PublishedDebugSymbols -Directory $publish -OutputRoot $destination
    # Inspect every separately published component, not just the main dependency graph.
    $forbidden=Get-ChildItem $publish -Recurse -File | Where-Object {
        $_.Extension -in @('.pdb','.knxproj','.knxprod','.vd2','.vd3','.vd4','.vd5','.pfx','.p12','.key') -or
        $_.Name -match '(?i)^(settings|consents|registry|private-key|update-key)\.' -or $_.Name -match '(?i)private.*\.pem$'
    }
    if ($forbidden) { throw 'Publish output contains private data, symbols or a signing key.' }
    if(!$IsWindows -and $rid.StartsWith('linux')){
        foreach($executable in @($MainExe,'createdump','ScriptWorker/Knx.Scripting','ScriptWorker/createdump','UpdateBroker/BusChord.UpdateBroker','UpdateBroker/createdump')){
            $file=Join-Path $publish $executable
            if(Test-Path -LiteralPath $file){[IO.File]::SetUnixFileMode($file,[IO.UnixFileMode]493)}
        }
    }
    if (Test-Path (Join-Path $source 'LICENSE')) { Copy-Item (Join-Path $source 'LICENSE') (Join-Path $publish 'LICENSE') }
    $iconNotice=Join-Path $source 'src/Knx.App/Assets/ICONS.md'
    if(Test-Path $iconNotice){Copy-Item $iconNotice (Join-Path $publish 'ICON-NOTICES.md')}
    $projectName=[IO.Path]::GetFileNameWithoutExtension($Project)
    $assets=Get-ChildItem $build -Recurse -Filter project.assets.json | Where-Object DirectoryName -Match ([regex]::Escape($projectName)) | Select-Object -First 1
    if(!$assets){throw 'Resolved dependency metadata is required for third-party notices.'}
    & (Join-Path $PSScriptRoot 'Write-Notices.ps1') -Assets $assets.FullName -Destination (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -Runtime $rid
    if($Production -and $rid.StartsWith('win')){
        if(!$env:BUSCHORD_SIGN_TEMPLATE){throw 'Production Windows packages require publisher code signing.'}
        foreach($file in Get-ChildItem $publish -Recurse -File | Where-Object Extension -In @('.exe','.dll')){
            if((Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -eq 'Valid'){continue}
            $quoted="'"+$file.FullName.Replace("'","''")+"'"
            $command=$env:BUSCHORD_SIGN_TEMPLATE.Replace('{{file}}',$quoted)
            & ([scriptblock]::Create($command))
            if($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid'){throw 'Payload Authenticode signing failed.'}
        }
    }
    & $DotNet run --project (Join-Path $SourceRoot 'tools-src/BusChord.Release') --artifacts-path (Join-Path $destination 'build/inventory-generator') @trustProps -- `
        inventory $publish $AppId $Version $rid "$rid-$Channel"
    if($LASTEXITCODE -ne 0){throw 'Installed code inventory generation failed.'}
    $release=Join-Path $destination "releases/$rid"
    $vpkArgs=@('pack','--packId',$AppId,'--packVersion',$Version,'--packDir',$publish,'--runtime',$rid,
        '--channel',"$rid-$Channel",'--mainExe',$(if($rid.StartsWith('win')){"$MainExe.exe"}else{$MainExe}),
        '--packTitle',$AppId,'--outputDir',$release,'--icon',$(if($rid.StartsWith('win')){$WindowsIcon}else{$Icon}),'--delta','None','--noDefaultExclude')
    if ($rid.StartsWith('linux')) { $vpkArgs=@('[linux]')+$vpkArgs }
    if ($Production -and $rid.StartsWith('win')) {
        if (!$env:BUSCHORD_SIGN_TEMPLATE) { throw 'Production Windows packages require publisher code signing.' }
        # Payload is already signed/inventoried. Only SDK-generated setup/stub/updater files may change.
        $vpkArgs+=@('--signTemplate',$env:BUSCHORD_SIGN_TEMPLATE,'--signExclude','(?i)^(?!.*[\\/](?:Squirrel|Setup|Update|[^\\/]+_ExecutionStub)\.exe$).*\.(?:exe|dll)$')
    }
    if($Vpk -eq 'vpk'){ & $DotNet tool run vpk -- @vpkArgs }
    else { & $Vpk @vpkArgs }
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed: $rid" }
    if($rid.StartsWith('win')){
        $proof=Join-Path $destination ('inventory-proof/'+[guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($proof)|Out-Null
        $portable=Join-Path $release "$AppId-$rid-$Channel-Portable.zip"
        [IO.Compression.ZipFile]::ExtractToDirectory($portable,$proof)
        & $DotNet run --project (Join-Path $SourceRoot 'tools-src/BusChord.Release') --artifacts-path (Join-Path $destination 'build/inventory-generator') @trustProps -- `
            check-inventory (Join-Path $proof 'current') $AppId $Version $rid
        if($LASTEXITCODE -ne 0){throw 'Packaged code differs from its scoped inventory; the artifact must not be distributed.'}
        if(Test-Path $workerProject){
            & (Join-Path $workerProbe 'BusChord.ScriptWorkerProbe.exe') (Join-Path $proof 'current')
            if($LASTEXITCODE -ne 0){throw 'The extracted portable script worker did not execute safely without an installed .NET runtime.'}
        }
    }
}
Get-ChildItem (Join-Path $destination 'releases') -Recurse -File | Get-FileHash -Algorithm SHA256 |
    Select-Object @{Name='File';Expression={[IO.Path]::GetRelativePath($destination,$_.Path)}},Hash |
    ConvertTo-Json | Set-Content (Join-Path $destination 'package-hashes.json') -Encoding utf8
