namespace GodDB.App.Infrastructure;

// 라이선스 화면 데이터. 의존성을 바꾸면 이 파일과 저장소의 THIRD_PARTY_NOTICES.md 를 함께 갱신한다.

public sealed record ThirdPartyLicense(string Name, string Version, string License, string Copyright, string Url, string? Text, string Note)
{
    public string Id => Name;
}

public static class Licenses
{
    public const string GodDBCopyright = "Copyright (c) 2026 zio0911";
    public const string GodDBUrl = "https://github.com/zio0911/god_db_win";

    public static readonly IReadOnlyList<ThirdPartyLicense> ThirdParty =
    [
        new(".NET", "10", "MIT", "Copyright (c) .NET Foundation and Contributors", "https://github.com/dotnet/runtime",
            Mit("Copyright (c) .NET Foundation and Contributors"), "런타임 (앱에 포함해 배포 — PC 에 .NET 을 설치하지 않아도 돼요)"),
        new("Avalonia", "12.1.6", "MIT", "Copyright (c) AvaloniaUI OÜ", "https://github.com/AvaloniaUI/Avalonia",
            Mit("Copyright (c) AvaloniaUI OÜ"), "화면 (UI 프레임워크)"),
        new("AvaloniaEdit", "12.0.0", "MIT", "Copyright (c) AvaloniaUI OÜ, Copyright (c) AlphaSierraPapa for the SharpDevelop Team",
            "https://github.com/AvaloniaUI/AvaloniaEdit",
            Mit("Copyright (c) AvaloniaUI OÜ\nCopyright (c) AlphaSierraPapa for the SharpDevelop Team"), "SQL 편집기"),
        new("Avalonia.Controls.DataGrid", "12.1.2", "MIT", "Copyright (c) AvaloniaUI OÜ", "https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid",
            Mit("Copyright (c) AvaloniaUI OÜ"), "결과 표"),
        new("CommunityToolkit.Mvvm", "8.4.2", "MIT", "Copyright (c) .NET Foundation and Contributors", "https://github.com/CommunityToolkit/dotnet",
            Mit("Copyright (c) .NET Foundation and Contributors"), ""),
        new("MySqlConnector", "3.0.0", "MIT", "Copyright (c) 2016 Bradley Grainger", "https://github.com/mysql-net/MySqlConnector",
            Mit("Copyright (c) 2016 Bradley Grainger"), "MySQL 드라이버"),
        new("Npgsql", "10.0.3", "PostgreSQL", "Copyright (c) 2002-2025, Npgsql", "https://github.com/npgsql/npgsql",
            PostgreSqlLicense, "PostgreSQL 드라이버"),
        new("Microsoft.Data.SqlClient", "7.1.2", "MIT", "Copyright (c) Microsoft Corporation", "https://github.com/dotnet/SqlClient",
            Mit("Copyright (c) Microsoft Corporation"), "SQL Server 드라이버 (Windows 인증 지원)"),
        new("Velopack", "1.2.161", "MIT", "Copyright (c) 2024 Velopack Ltd.", "https://github.com/velopack/velopack",
            Mit("Copyright (c) 2024 Velopack Ltd."), "자동 업데이트"),
    ];

    public static string GodDB => Mit(GodDBCopyright);

    public static string Mit(string copyright) => $"""
MIT License

{copyright}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
""";

    public const string PostgreSqlLicense = """
Copyright (c) 2002-2025, Npgsql

Permission to use, copy, modify, and distribute this software and its
documentation for any purpose, without fee, and without a written agreement
is hereby granted, provided that the above copyright notice and this
paragraph and the following two paragraphs appear in all copies.

IN NO EVENT SHALL NPGSQL BE LIABLE TO ANY PARTY FOR DIRECT, INDIRECT,
SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, INCLUDING LOST PROFITS,
ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN IF
Npgsql HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

NPGSQL SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS ON AN "AS IS" BASIS, AND Npgsql
HAS NO OBLIGATIONS TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS,
OR MODIFICATIONS.
""";
}
