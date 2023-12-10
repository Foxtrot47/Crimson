# Crimson

**Highly WIP**

## A Simple Epic Games Launcher frontend made with WinUI3

## Downloads
There are not stable builds at the moment, you have build it yourself

## Development

1. Download and Install Visual Studio 2022
    Make sure to include .NET Desktop Development and Windows App SDK C# Templates

    Or you can just run 

    `winget install "Visual Studio Community 2022"  --override "--add Microsoft.VisualStudio.Workload.NativeDesktop  Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cpp"  -s msstore`

2. Clone this repo

3. Build and run the app
 - If you want to have loosely packaged binaries, you can just build the main Crimson project and run the Crimson.exe
 - If you want to get application packaged as appx , you must add a signing key to Crimson Packaging project and run publish from that project