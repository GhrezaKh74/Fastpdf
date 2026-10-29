#!/bin/bash
# Build, test and pack FastPdfWriterStream + FastPdfWriterStream.Designer NuGet packages
set -e

echo "=== Building ==="
dotnet build FastPdfWriterStream.sln -c Release

echo ""
echo "=== Running Tests ==="
dotnet test tests/FastPdfWriterStream.Tests/FastPdfWriterStream.Tests.csproj -c Release --no-build

echo ""
echo "=== Packing NuGet ==="
dotnet pack src/FastPdfWriterStream/FastPdfWriterStream.csproj -c Release --no-build -o ./nupkg
dotnet pack src/FastPdfWriterStream.Designer/FastPdfWriterStream.Designer.csproj -c Release --no-build -o ./nupkg

echo ""
echo "✅ Done! Packages are in ./nupkg/"
echo "   Install locally:  dotnet add package FastPdfWriterStream --source ./nupkg"
