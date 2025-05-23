# Building coredistools

## Building on Windows with Visual Studio 2022

1. Checkout the jitutils repository:
```
git clone https://github.com/dotnet/jitutils.git
cd jitutils
```

2. Checkout the LLVM project repository into a subdirectory named src/llvm-project:
```
git clone --depth 1 --branch llvmorg-20.1.0 https://github.com/llvm/llvm-project.git src\llvm-project
```

3. Build `llvm-tblgen.exe`:
```
build-tblgen.cmd
```

This builds llvm-tblgen.exe and puts it in the `bin` subdirectory.

4. Add the `bin` subdirectory to the `PATH`:
```
set "PATH=%cd%\bin;%PATH%"
```

This puts the just built lldb-tblgen.exe on the `PATH`.

5. Build `coredistools.dll` for a combination of target OS and architecture.
Build Windows x64, Windows x86, and Windows ARM64 binaries:
```
build-coredistools.cmd win-x64
build-coredistools.cmd win-x86
build-coredistools.cmd win-arm64
```

The file will be copied to subdirectory `artifacts` after the command finishes. E.g., for win-x64:
```
dir /A:-D /B /S artifacts\win-x64
F:\echesako\git\jitutils\artifacts\win-x64\bin\coredistools.dll
F:\echesako\git\jitutils\artifacts\win-x64\lib\coredistools.lib
```

### Building Debug binaries

The `build-coredistools.cmd` script is set up to build a Release build. To create a Debug build with a PDB file
for debugging, change the `--config Release` line to `--config Debug`.

## Building on Linux / Mac

1. Checkout the jitutils repository:
```
git clone https://github.com/dotnet/jitutils.git
cd jitutils
```

2. Checkout the LLVM project repository:
```
git clone --depth 1 --branch llvmorg-20.1.0 https://github.com/llvm/llvm-project.git src/llvm-project
```

3. Start the Docker container in which the build will be run:
```
docker run -it --rm --entrypoint /bin/bash -v ~/git/jitutils:/opt/code -w /opt/code mcr.microsoft.com/dotnet-buildtools/prereqs:azurelinux-3.0-net10.0-cross-amd64
```

4. Install necessary dependencies (in Docker):
You need to install the `ncurses-compat` package because the Azure Linux container we use doesn't have libtinfo.so.5, which the built
llvm-tblgen needs to be able to run. (Note that we build in the Azure Linux container, but we also run some built binaries, namely llvm-tblgen,
as part of the build process.)
```
sudo tdnf install -y ncurses-compat
```

5. Build `llvm-tblgen` (in Docker):
```
./build-tblgen.sh linux-x64 /crossrootfs/x64
```

This builds llvm-tblgen and puts it in the `bin` subdirectory.

6. Add `llvm-tblgen` to the PATH (in Docker):
```
export PATH=$(pwd)/bin:$PATH
```

7. Build `libcoredistools.so` for Linux x64 (in Docker):
```
./build-coredistools.sh linux-x64 /crossrootfs/x64
```

The file will be copied to subdirectory `artifacts` after the command finishes:
```
find ./artifacts -name libcoredistools.so
./artifacts/linux-x64/bin/libcoredistools.so
./artifacts/linux-x64/lib/libcoredistools.so
```

8. Build `libcoredistools.so` for Linux arm64 under Docker:
```
docker run -it --rm --entrypoint /bin/bash -v ~/git/jitutils:/opt/code -w /opt/code mcr.microsoft.com/dotnet-buildtools/prereqs:azurelinux-3.0-net10.0-cross-arm64
sudo tdnf install -y ncurses-compat
export PATH=$(pwd)/bin:$PATH
./build-coredistools.sh linux-arm64 /crossrootfs/arm64
```

9. Build `libcoredistools.so` for Linux arm under Docker:
```
docker run -it --rm --entrypoint /bin/bash -v ~/git/jitutils:/opt/code -w /opt/code mcr.microsoft.com/dotnet-buildtools/prereqs:azurelinux-3.0-net10.0-cross-arm
sudo tdnf install -y ncurses-compat
export PATH=$(pwd)/bin:$PATH
./build-coredistools.sh linux-arm /crossrootfs/arm
```

10. Build `libcoredistools.so` for Linux riscv64 under Docker:
There is no Azure Linux container for RISC-V so use the standard Ubuntu cross build container used for e.g. dotnet/runtime.
```
docker run -it --rm --entrypoint /bin/bash -v ~/git/jitutils:/opt/code -w /opt/code mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-22.04-cross-riscv64
apt install libtinfo5

# If you haven't built llvm-tblgen in step 5, you can do so in the same docker (pass "/" as crossrootfs).
./build-tblgen.sh linux-x64 /

# Now, the main course
export PATH=$(pwd)/bin:$PATH
./build-coredistools.sh linux-riscv64 /crossrootfs/riscv64
```
