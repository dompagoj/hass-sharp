"""Build a runtime-only integration ZIP within Home Assistant Marketplace limits."""

import argparse
import json
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


# The frontend's mock preview build (frontend/tests/preview-build.config.ts). Everything under www/ is served without
# authentication, so the package skips it and every other page a browser would open as HTML there.
PREVIEW_DIRECTORY = "visual-v2-check-baby"
PAGE_SUFFIXES = (".html", ".htm", ".xhtml")


def test_page(relative: Path) -> bool:
    """True for a file under www/ that the package skips: the preview build and any HTML page."""
    parts = relative.parts
    return parts[0] == "www" and (PREVIEW_DIRECTORY in parts[1:] or relative.suffix.lower() in PAGE_SUFFIXES)


def package_release(integration_dir: Path, output: Path, version: str) -> None:
    required = (
        "__init__.py", "manifest.json", "dotnet.runtimeconfig.json",
        "out/HassSharp.dll", "out/HassSharp.deps.json", "out/Python.Runtime.dll",
        "www/hass-sharp.js", "visual_node_catalog.json", "authoring_examples.json",
    )
    for name in required:
        if not (integration_dir / name).is_file():
            raise ValueError(f"Missing runtime file: {name}. Build the integration first.")

    files = [*integration_dir.glob("*.py"), *integration_dir.glob("*.json")]
    for directory in ("out", "www", "translations", "brand"):
        files.extend((integration_dir / directory).rglob("*"))
    files = sorted(
        path for path in files
        if path.is_file()
        and not any(part.startswith(".") or part == "__pycache__"
                    for part in path.relative_to(integration_dir).parts)
        and not test_page(path.relative_to(integration_dir))
    )
    manifest = json.loads((integration_dir / "manifest.json").read_text(encoding="utf-8"))
    manifest["version"] = version.removeprefix("v")
    manifest_bytes = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
    unpacked_size = sum(
        len(manifest_bytes) if path.name == "manifest.json" and path.parent == integration_dir
        else path.stat().st_size for path in files
    )
    if unpacked_size > 100 * 1024 * 1024:
        raise ValueError(f"Runtime files exceed the Marketplace 100 MiB limit: {unpacked_size} bytes")
    if len(files) > 50_000:
        raise ValueError("Runtime files exceed the Marketplace 50,000 file limit")

    output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(output, "w", compression=ZIP_DEFLATED) as archive:
        for path in files:
            name = path.relative_to(integration_dir).as_posix()
            if name == "manifest.json":
                archive.writestr(name, manifest_bytes)
            else:
                archive.write(path, name)
    if output.stat().st_size > 100 * 1024 * 1024:
        output.unlink()
        raise ValueError("Release ZIP exceeds the Marketplace 100 MiB download limit")
    print(f"Packaged {len(files)} files: {output.stat().st_size} bytes ZIP, {unpacked_size} bytes unpacked")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--integration-dir", type=Path, default=Path("custom_components/hass_sharp"))
    parser.add_argument("--output", type=Path, default=Path("dist/hass_sharp.zip"))
    parser.add_argument("--version", required=True, help="Release tag, optionally prefixed with v")
    args = parser.parse_args()
    try:
        package_release(args.integration_dir, args.output, args.version)
    except ValueError as error:
        parser.error(str(error))
