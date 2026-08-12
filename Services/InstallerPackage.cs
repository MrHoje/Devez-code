using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DevezCode.Services;

public static class InstallerPackage
{
    public static string ExtractSetup(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();

        if (files.Length != 1)
            throw new InvalidDataException("설치 파일 압축에는 실행 파일이 정확히 하나 있어야 합니다.");

        var entry = files[0];
        if (!string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal)
            || !entry.Name.StartsWith("DevezCode_Setup_", StringComparison.OrdinalIgnoreCase)
            || !entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || entry.Length <= 0)
            throw new InvalidDataException("설치 파일 압축 구조가 올바르지 않습니다.");

        var setupPath = Path.Combine(destinationDirectory, entry.Name);
        entry.ExtractToFile(setupPath, overwrite: true);
        return setupPath;
    }
}
