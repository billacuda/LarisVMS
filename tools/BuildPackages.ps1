# Shared by build-installers.ps1 and install-web.ps1: where build-node.ps1 and build-proxy.ps1 leave
# each platform's build, and how they're bundled into a Web publish's packages\ folder.
# Dot-source it: . (Join-Path $PSScriptRoot 'tools\BuildPackages.ps1')

# Where build-node.ps1 and build-proxy.ps1 leave each platform's package and its server-side files,
# relative to the repository root.
$script:NodeProxyBuildOutputs = @{
    'win-x64'   = @{ Node = 'publish\LarisVMS.Node\win';   NodeMeta = 'publish\LarisVMS.Node'
                     Proxy = 'publish\LarisVMS.Proxy\win'; ProxyMeta = 'publish\LarisVMS.Proxy'; Exe = '.exe'
                     Cuda = 'onnxruntime_providers_cuda.dll' }
    'linux-x64' = @{ Node = 'publish\LarisVMS.Node\linux';   NodeMeta = 'publish\LarisVMS.Node\linux-x64-meta'
                     Proxy = 'publish\LarisVMS.Proxy\linux'; ProxyMeta = 'publish\LarisVMS.Proxy\linux-x64-meta'; Exe = ''
                     Cuda = 'libonnxruntime_providers_cuda.so' }
}

<#
.SYNOPSIS
    Copies every platform's node and proxy build into <PackagesDir>\<kind>\<platform>\, with every
    platform's CUDA provider in <PackagesDir>\cuda-provider\<platform>\. The Web server registers the
    builds as Pending at startup (BundledBuildRegistrar) and serves the CUDA providers to NVIDIA nodes.
    A file that wasn't built (no Vision Service, no publisher key) is skipped.
#>
function Copy-NodeProxyPackages {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$PackagesDir,
        [Parameter(Mandatory)][string[]]$Platforms
    )
    foreach ($platform in $Platforms) {
        $out = $script:NodeProxyBuildOutputs[$platform]
        $bundles = @(
            @{ Src = "$($out.Node)\LarisVMS.Node$($out.Exe)";                          Dest = "node\$platform" },
            @{ Src = "$($out.Node)\LarisVMS.Vision.Service$($out.Exe)";                Dest = "node\$platform" },
            @{ Src = "$($out.NodeMeta)\node-build-version.txt";                        Dest = "node\$platform" },
            @{ Src = "$($out.NodeMeta)\publisher.sig";                                 Dest = "node\$platform" },
            @{ Src = "$($out.NodeMeta)\backends\onnxruntime-backends.zip";             Dest = "node\$platform" },
            @{ Src = "$($out.NodeMeta)\backends\backends-stamp.txt";                   Dest = "node\$platform" },
            @{ Src = "$($out.NodeMeta)\backends\backends.publisher.sig";               Dest = "node\$platform" },
            @{ Src = "$($out.Proxy)\LarisVMS.Proxy$($out.Exe)";                        Dest = "proxy\$platform" },
            @{ Src = "$($out.ProxyMeta)\proxy-build-version.txt";                      Dest = "proxy\$platform" },
            @{ Src = "$($out.ProxyMeta)\publisher.sig";                                Dest = "proxy\$platform" },
            # ~300 MB each, MIT-licensed like the rest of ONNX Runtime, so the server serves NVIDIA
            # nodes on either OS without downloading anything (VisionNativeDistributor).
            @{ Src = "$($out.NodeMeta)\cuda-provider\$($out.Cuda)";                    Dest = "cuda-provider\$platform" },
            @{ Src = "$($out.NodeMeta)\cuda-provider\$($out.Cuda).publisher.sig";      Dest = "cuda-provider\$platform" }
        )
        foreach ($b in $bundles) {
            $src = Join-Path $RepoRoot $b.Src
            if (-not (Test-Path $src)) { Write-Host "    (not built, skipped) $($b.Src)" -ForegroundColor DarkGray; continue }
            $dest = Join-Path $PackagesDir $b.Dest
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            Copy-Item $src $dest -Force
        }
    }
}
