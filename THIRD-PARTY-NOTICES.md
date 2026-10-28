# Third-party notices

MarkPortO itself is licensed under the GNU General Public License, version 3 only (GPL-3.0-only); see [`LICENSE`](LICENSE). The packages below are under MIT or Apache-2.0, which can be combined with GPL-3.0.

MarkPortO uses the packages below. The license of each is the one declared in its NuGet package (checked against the package's `.nuspec`). The versions are the ones pinned in [`Directory.Packages.props`](Directory.Packages.props).

## Packages that ship with MarkPortO

| Package | Version | License | Used for |
|---|---|---|---|
| DocumentFormat.OpenXml | 3.5.1 | MIT | Reading docx / xlsx / pptx, and making the temporary copies used for printing |
| PdfPig | 0.1.16 | Apache-2.0 | Reading the text of each PDF page (page assignment for docx) |
| Markdig | 1.4.2 | BSD-2-Clause (© Alexandre Mutel) | Drawing the Markdown of a page in the preview |
| Microsoft.Web.WebView2 | 1.0.4258.31 | Microsoft WebView2 SDK license (see the package; the runtime is the Microsoft Edge WebView2 Runtime of Windows) | Showing the drawn Markdown in the preview |
| System.Drawing.Common | 10.0.13 | MIT | Making and saving images |
| Microsoft.Extensions.Logging.Abstractions | 10.0.13 | MIT | Logging interfaces |
| System.Security.Cryptography.ProtectedData | 10.0.13 | MIT | Protecting access keys typed on the screen (DPAPI, current user) |
| System.CommandLine | 2.0.13 | MIT | The command line |

These packages bring in the following packages, which ship with MarkPortO too. All of them are MIT, © Microsoft Corporation (checked against each `.nuspec`): DocumentFormat.OpenXml.Framework 3.5.1, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.13, Microsoft.Win32.SystemEvents 10.0.13, System.IO.Packaging 10.0.2.

Copyright of the Microsoft packages: © Microsoft Corporation. All rights reserved.

## Used only to build and test (not shipped)

| Package | License |
|---|---|
| Microsoft.NET.Test.Sdk | MIT |
| xunit, xunit.runner.visualstudio | Apache-2.0 |

## Not bundled

MarkPortO does not include or redistribute the following. It starts the copy installed on the computer.

- Microsoft Office (Word, Excel, PowerPoint): used through COM to make PDFs. Needs its own license.
- LibreOffice: used in headless mode to make PDFs ([Mozilla Public License 2.0](https://www.libreoffice.org/about-us/licenses/)).
- Windows (`Windows.Data.Pdf`): turns PDF pages into images.

The full license text of each package is in the package itself (the `.nupkg`, or `~/.nuget/packages/<name>/<version>/`).
