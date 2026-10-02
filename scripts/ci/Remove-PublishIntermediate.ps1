$sourceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\src')).Path
$sourcePrefix = $sourceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

# 次の publish では BaseIntermediateOutputPath が変わり、前回の obj 内の生成 C# が Compile に混入する。
# 公開済みアプリは output 側に残したまま、自己完結版の中間ファイルだけを除去する。
Get-ChildItem -LiteralPath $sourceRoot -Filter '*.csproj' -File -Recurse | ForEach-Object {
    $candidate = Join-Path $_.DirectoryName 'obj\publish-sc'
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        return
    }

    $target = (Resolve-Path -LiteralPath $candidate).Path
    $parent = Split-Path -Parent $target
    if (-not $target.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $target) -ne 'publish-sc' -or
        (Split-Path -Leaf $parent) -ne 'obj') {
        throw "Unexpected publish intermediate path: $target"
    }

    Remove-Item -LiteralPath $target -Recurse -Force
}
