import os
import platform
import tarfile
import urllib.request
import tempfile
import shutil

from .const import logger

# Target .NET version
DOTNET_VERSION = "10.0.101"

def get_architecture():
    machine = platform.machine().lower()
    if machine in ("x86_64", "amd64"):
        return "x64"
    if machine in ("aarch64", "arm64"):
        return "arm64"
    if machine.startswith("arm"):
        # Home Assistant often runs on armhf or armv7l
        return "arm"
    return None

def get_download_url(version, arch):
    # This URL pattern is for the .NET SDK binaries
    # Example: https://dotnetcli.azureedge.net/dotnet/Sdk/10.0.101/dotnet-sdk-10.0.101-linux-x64.tar.gz
    # Note: For non-linux platforms, this might need adjustment, but HASS usually runs on Linux.
    system = platform.system().lower()
    if system == "darwin":
        return f"https://dotnetcli.azureedge.net/dotnet/Sdk/{version}/dotnet-sdk-{version}-osx-{arch}.tar.gz"

    # Detect Alpine Linux
    is_alpine = os.path.exists("/etc/alpine-release")
    
    os_name = "linux"
    if is_alpine:
        os_name = "linux-musl"
    
    return f"https://dotnetcli.azureedge.net/dotnet/Sdk/{version}/dotnet-sdk-{version}-{os_name}-{arch}.tar.gz"

def install_dotnet(install_dir: str):
    # Check if the sdk version we want is already there
    # The dotnet executable is in the root of install_dir
    # The SDKs are in install_dir/sdk/VERSION
    sdk_path = os.path.join(install_dir, "sdk", DOTNET_VERSION)
    dotnet_exe = os.path.join(install_dir, "dotnet")
    
    if os.path.exists(sdk_path) and os.path.exists(dotnet_exe):
        # On Linux, try to run it to see if it's the right one (e.g. not glibc on musl)
        try:
            import subprocess
            result = subprocess.run([dotnet_exe, "--version"], capture_output=True, text=True, timeout=5)
            if result.returncode == 0:
                logger.info(".NET SDK %s already installed and working in %s", DOTNET_VERSION, install_dir)
                return True
            else:
                logger.warning(".NET SDK found but not working (exit code %s). Re-installing...", result.returncode)
        except Exception as e:
            logger.warning(".NET SDK found but failed to run: %s. Re-installing...", e)
    
    if os.path.exists(install_dir):
        logger.info("Cleaning up existing .NET directory before re-installation")
        shutil.rmtree(install_dir)

    arch = get_architecture()
    if not arch:
        logger.error("Unsupported architecture: %s", platform.machine())
        return False

    # Log environment info to help debugging
    system = platform.system().lower()
    is_alpine = os.path.exists("/etc/alpine-release")
    logger.info("Detected environment: %s, machine: %s, arch: %s, is_alpine: %s", 
                system, platform.machine(), arch, is_alpine)

    url = get_download_url(DOTNET_VERSION, arch)
    logger.info("Downloading .NET SDK from %s", url)

    try:
        with tempfile.TemporaryDirectory() as temp_dir:
            tar_path = os.path.join(temp_dir, "dotnet.tar.gz")
            
            # Use a longer timeout for the download
            request = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
            with urllib.request.urlopen(request, timeout=300) as response, open(tar_path, 'wb') as out_file:
                shutil.copyfileobj(response, out_file)
            
            logger.info("Extracting .NET SDK to %s", install_dir)
            if not os.path.exists(install_dir):
                os.makedirs(install_dir, exist_ok=True)
            
            with tarfile.open(tar_path, "r:gz") as tar:
                tar.extractall(path=install_dir)
        
        # Ensure the dotnet executable is runnable
        dotnet_exe = os.path.join(install_dir, "dotnet")
        if os.path.exists(dotnet_exe):
            os.chmod(dotnet_exe, 0o755)
            
        logger.info(".NET SDK installed successfully")
        return True
    except Exception as e:
        logger.error("Failed to download and install .NET SDK: %s", e)
        return False
