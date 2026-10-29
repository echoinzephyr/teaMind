# Third-party components

teaMind uses the following third-party components. The package lock files record exact resolved versions. Package dependencies retain their own licenses; this file does not replace those licenses.

- Microsoft Windows App SDK / WinUI 2.6.4 and its component dependencies — Microsoft package license. See the `license.txt` distributed in the official NuGet packages and https://github.com/microsoft/WindowsAppSDK .
- Microsoft Graphics Win2D 1.4.0 — MIT. https://github.com/microsoft/Win2D .
- PDFsharp 6.2.4 and PDFsharp dependencies — MIT. https://docs.pdfsharp.net/General/License/License.html .
- .NET 10 runtime and System.Security.Cryptography.ProtectedData — MIT; runtime also includes third-party notices. https://github.com/dotnet/runtime .
- Microsoft Windows SDK BuildTools — Microsoft package license; used during builds.
- Microsoft.Web.WebView2 is a transitive WinUI dependency. teaMind does not embed a WebView or use it for its editor.

Windows system fonts are loaded from the user's Windows installation and are not redistributed. User documents, images and attachments remain the user's own content.

Xmind is a trademark of its respective owner. teaMind is an independent application and does not include Xmind application code, artwork, or proprietary assets. Its file converter uses public format references, including https://github.com/xmindltd/xmind-sdk-js .
