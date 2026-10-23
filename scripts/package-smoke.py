"""Build/test/pack and exercise fresh consumers. No feed writes; Python standard library only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--version", required=True)
parser.add_argument("--output", default="artifacts/validation/packages")
parser.add_argument("--sdk", default="10.0.402")
parser.add_argument("--skip-build", action="store_true", help="Only after this exact version's Release build/tests passed")
parser.add_argument("--baseline-version", help="Also measure public API workloads against this released NuGet version")
args = parser.parse_args()
OUT = (ROOT / args.output).resolve()
OUT.mkdir(parents=True, exist_ok=True)
WORK = Path(tempfile.mkdtemp(prefix="consumers-", dir=OUT))
PACKAGES = WORK / "feed"
PACKAGES.mkdir()
env = dict(os.environ, NUGET_PACKAGES=str(WORK / "cache"))
manifest = {"version": args.version, "commands": [], "checks": [], "artifacts": {}, "status": "running"}


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def run(*command, cwd=WORK):
    result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    log = OUT / f"command-{len(manifest['commands']):02}.log"
    write(log, result.stdout)
    manifest["commands"].append({"argv": list(map(str, command)), "exit": result.returncode, "log": log.name})
    print(f"{command[0]} {command[1]}: exit {result.returncode}", flush=True)
    if result.returncode:
        raise RuntimeError(f"Command failed; see {log}\n{result.stdout[-5000:]}")
    return result.stdout


def check(condition, description):
    manifest["checks"].append({"check": description, "pass": bool(condition)})
    if not condition:
        raise AssertionError(description)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def project(name, reference, source, executable=True):
    folder = WORK / name
    write(folder / f"{name}.csproj", f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<OutputType>{'Exe' if executable else 'Library'}</OutputType></PropertyGroup>
<ItemGroup>{reference}</ItemGroup></Project>''')
    write(folder / "Program.cs", source)
    return folder / f"{name}.csproj"


CONSUMER = r'''
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsharpBgapi;
using Microsoft.Extensions.DependencyInjection;

var root = AppContext.BaseDirectory;
using var services = new ServiceCollection().AddCsharpBgapi(o => o.ResponseTimeoutSeconds = 7).BuildServiceProvider();
var device = services.GetRequiredService<BgapiDevice>();
if (!ReferenceEquals(device, services.GetRequiredService<BgapiDevice>()) || device.ResponseTimeout.TotalSeconds != 7)
    throw new Exception("DI lifetime/options failed");
if (!device.Definitions.HasDefinitions) throw new Exception("Embedded loading failed");
using var external = new BgapiDevice();
var assembly = typeof(BgapiDevice).Assembly;
var rows = new List<object>();
foreach (var file in new[] { "sl_bt.xapi", "sl_btmesh.xapi" })
{
    var matches = Directory.GetFiles(root, file, SearchOption.AllDirectories);
    if (matches.Length != 1) throw new Exception($"Expected exactly one external {file}, got {matches.Length}");
    external.LoadXapi(matches[0]);
    using var resource = assembly.GetManifestResourceStream("CsharpBgapi.Xapi." + file) ?? throw new Exception("Missing resource");
    var embeddedHash = Convert.ToHexString(SHA256.HashData(resource));
    var externalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(matches[0])));
    if (embeddedHash != externalHash) throw new Exception("External/embedded resource mismatch");
    rows.Add(new { file, sha256 = externalHash });
}
if (!external.Definitions.HasDefinitions) throw new Exception("External loading failed");
// Check a real command from each API, without sending anything to hardware.
device.Protocol.EncodeCommand("bt", "system", "halt");
external.Protocol.EncodeCommand("btmesh", "mbt_client", "query_block_status");
string? sourceLink = null;
if (args.Length > 0)
{
    using var stream = File.OpenRead(args[0]);
    using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
    var metadata = provider.GetMetadataReader();
    foreach (var handle in metadata.CustomDebugInformation)
    {
        var item = metadata.GetCustomDebugInformation(handle);
        if (metadata.GetGuid(item.Kind) == new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A"))
            sourceLink = Encoding.UTF8.GetString(metadata.GetBlobBytes(item.Value));
    }
    if (sourceLink is null || !JsonDocument.Parse(sourceLink).RootElement.GetProperty("documents").EnumerateObject().Any())
        throw new Exception("Missing SourceLink mapping");
    if (metadata.Documents.Count == 0) throw new Exception("Missing PDB source documents");
}
Console.WriteLine(JsonSerializer.Serialize(new { resources = rows, sourceLink,
    assembly = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }));
'''

try:
    # The generated workspace must not inherit the repository's central package management or target list.
    write(WORK / "Directory.Build.props", "<Project/>")
    write(WORK / "Directory.Packages.props", "<Project/>")
    write(WORK / "global.json", json.dumps({"sdk": {"version": args.sdk, "rollForward": "latestPatch", "allowPrerelease": False}}))
    write(WORK / "NuGet.Config", f'''<configuration><packageSources><clear/>
<add key="candidate" value="{escape(str(PACKAGES))}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>
</packageSources><auditSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></auditSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="CsharpBgapi"/><package pattern="Validation.Intermediary"/></packageSource>
<packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>''')
    manifest["sdk"] = run("dotnet", "--version").strip()
    manifest["environment"] = run("dotnet", "--info")
    manifest["commit"] = run("git", "-C", ROOT, "rev-parse", "HEAD").strip()
    manifest["dirty"] = run("git", "-C", ROOT, "status", "--short")
    version = f"-p:Version={args.version}"
    config = f"-p:RestoreConfigFile={WORK / 'NuGet.Config'}"
    if not args.skip_build:
        run("dotnet", "restore", ROOT / "CsharpBgapi.slnx", "--configfile", WORK / "NuGet.Config")
        run("dotnet", "build", ROOT / "CsharpBgapi.slnx", "--configuration", "Release", "--no-restore", version)
        run("dotnet", "test", ROOT / "CsharpBgapi.slnx", "--configuration", "Release", "--no-build",
            "--logger", "trx;LogFileName=validation.trx", "--results-directory", OUT / "tests")
    run("dotnet", "pack", ROOT / "src/CsharpBgapi/CsharpBgapi.csproj", "--configuration", "Release",
        "--no-build", "--output", PACKAGES, version)
    package = PACKAGES / f"CsharpBgapi.{args.version}.nupkg"
    symbols = PACKAGES / f"CsharpBgapi.{args.version}.snupkg"
    for artifact in [package, symbols]:
        manifest["artifacts"][str(artifact.relative_to(OUT))] = sha(artifact)
    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        required = {"lib/net10.0/CsharpBgapi.dll", "lib/net10.0/CsharpBgapi.xml", "README.md",
                    "xapi/sl_bt.xapi", "xapi/sl_btmesh.xapi", "build/CsharpBgapi.targets",
                    "buildTransitive/CsharpBgapi.targets", "CsharpBgapi.nuspec"}
        check(required <= names, "nupkg contains framework assembly, docs, README, XAPI and both targets")
        check(not any(n.startswith("lib/") and not n.startswith("lib/net10.0/") for n in names), "only net10.0 target")
        nuspec = ET.fromstring(archive.read("CsharpBgapi.nuspec"))
        ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
        check(nuspec.findtext("n:metadata/n:version", namespaces=ns) == args.version, "package version matches tested build")
        check(nuspec.findtext("n:metadata/n:license", namespaces=ns) == "MIT", "MIT license metadata")
        dependencies = {e.attrib["id"] for e in nuspec.findall(".//n:dependency", ns)}
        check({"System.IO.Ports", "Microsoft.Extensions.Options", "Microsoft.Extensions.Logging.Abstractions",
               "Microsoft.Extensions.DependencyInjection.Abstractions"} <= dependencies, "runtime dependencies declared")
        check("Microsoft.SourceLink.GitHub" not in dependencies, "SourceLink stays a private build dependency")
        check(hashlib.sha256(archive.read("lib/net10.0/CsharpBgapi.dll")).hexdigest() ==
              sha(ROOT / "src/CsharpBgapi/bin/Release/net10.0/CsharpBgapi.dll"), "packed assembly equals tested assembly")
        manifest["packageEntries"] = sorted(names)
    with zipfile.ZipFile(symbols) as archive:
        check("lib/net10.0/CsharpBgapi.pdb" in archive.namelist(), "portable PDB present in snupkg")
        pdb = WORK / "CsharpBgapi.pdb"
        pdb.write_bytes(archive.read("lib/net10.0/CsharpBgapi.pdb"))
    candidate_ref = f'<PackageReference Include="CsharpBgapi" Version="{escape(args.version)}"/>'
    di_version = ET.parse(ROOT / "Directory.Packages.props").find(".//PackageVersion[@Include='Microsoft.Extensions.DependencyInjection']").attrib["Version"]
    di_ref = f'<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="{di_version}"/>'
    intermediary = project("Validation.Intermediary", candidate_ref, "public sealed class Marker { }", False)
    run("dotnet", "pack", intermediary, "-c", "Release", "-o", PACKAGES, "-p:Version=1.0.0-validation", config)
    for mode, reference in [
        ("Direct", candidate_ref),
        ("Transitive", '<PackageReference Include="Validation.Intermediary" Version="1.0.0-validation"/>'),
        ("Project", f'<ProjectReference Include="{escape(str(ROOT / "src/CsharpBgapi/CsharpBgapi.csproj"))}"/>')]:
        consumer = project(mode, reference + di_ref, CONSUMER)
        run("dotnet", "build", consumer, "-c", "Release", config, version)
        assembly = consumer.parent / "bin/Release/net10.0" / f"{mode}.dll"
        result = run("dotnet", assembly, pdb)
        check(json.loads(result)["assembly"].startswith(args.version + "+"), f"{mode}: exact candidate assembly loaded")
        manifest["checks"].append({"check": f"{mode}: build resource hashes and SourceLink", "evidence": json.loads(result), "pass": True})
        publish = consumer.parent / "publish"
        run("dotnet", "publish", consumer, "-c", "Release", "--no-build", "-o", publish)
        run("dotnet", publish / f"{mode}.dll")
        check(True, f"{mode}: publish resources and embedded/external loading")
        if mode != "Project":
            assets = json.loads((consumer.parent / "obj/project.assets.json").read_text())
            check(f"CsharpBgapi/{args.version}" in assets["libraries"], f"{mode}: local candidate resolved")
            cached = WORK / "cache/csharpbgapi" / args.version / f"csharpbgapi.{args.version}.nupkg"
            check(sha(cached) == sha(package), f"{mode}: restored package hash equals local candidate")
    # Use a fresh output directory: disabling copy must not pass because earlier files survived.
    toggle = project("NoCopy", candidate_ref, 'using CsharpBgapi; using var d = new BgapiDevice(); d.LoadDefaultXapis(); if (!d.Definitions.HasDefinitions) throw new Exception();')
    run("dotnet", "build", toggle, "-c", "Release", config, "-p:CsharpBgapiCopyXapi=false")
    check(not list((toggle.parent / "bin").rglob("*.xapi")), "copy opt-out leaves embedded loading available and omits external files")
    run("dotnet", toggle.parent / "bin/Release/net10.0/NoCopy.dll")
    observer = project("HardwareObserver", candidate_ref, (ROOT / "scripts/HardwareObservation.cs").read_text())
    run("dotnet", "build", observer, "-c", "Release", config)
    check(True, "hardware observer compiles against candidate public API (not run)")
    if args.baseline_version:
        # Exact-package comparison uses only the public API common to both releases.
        # It cannot measure new internal seams against an old binary.
        write(WORK / "NuGet.Baseline.Config", '''<configuration><packageSources><clear/>
<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/></packageSourceMapping></configuration>''')
        for label, ref, cfg in [("CandidateBenchmark", candidate_ref, WORK / "NuGet.Config"),
                               ("BaselineBenchmark", f'<PackageReference Include="CsharpBgapi" Version="{escape(args.baseline_version)}"/>', WORK / "NuGet.Baseline.Config")]:
            bench = project(label, ref, (ROOT / "scripts/PublicMeasurements.cs").read_text())
            run("dotnet", "build", bench, "-c", "Release", f"-p:RestoreConfigFile={cfg}")
            result = run("dotnet", bench.parent / "bin/Release/net10.0" / f"{label}.dll")
            write(OUT / f"{label}.json", result)
            manifest["artifacts"][f"{label}.json"] = sha(OUT / f"{label}.json")
        baseline = WORK / "cache/csharpbgapi" / args.baseline_version / f"csharpbgapi.{args.baseline_version}.nupkg"
        manifest["baseline"] = {"version": args.baseline_version, "sha256": sha(baseline)}
    manifest["status"] = "pass"
except Exception as error:
    manifest["status"] = "fail"
    manifest["error"] = str(error)
    raise
finally:
    write(OUT / "manifest.json", json.dumps(manifest, indent=2))
    print(f"Evidence: {OUT / 'manifest.json'}", flush=True)
    # Each run's workspace holds a full NuGet cache and every generated consumer, and used to be
    # left behind. Keep only the feed, whose packages manifest.json hashes; logs are already in OUT.
    for entry in WORK.iterdir():
        if entry.name == "feed":
            continue
        if entry.is_dir():
            shutil.rmtree(entry, ignore_errors=True)
        else:
            entry.unlink(missing_ok=True)
