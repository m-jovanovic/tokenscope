# Notices shipped with the app image

TokenScope's MIT license is in `/app/LICENSE`. Third-party components retain their own licenses.

Vite writes notices for the JavaScript modules it bundles to `/app/wwwroot/third-party-licenses.txt`. The Docker build also copies the installed Tailwind CSS license, es-toolkit NOTICE, and victory-vendor's nested D3 and internmap licenses under `/app/licenses/`. The victory-vendor npm package omits its project's root license; the [included copy](victory-vendor/LICENSE.txt) is from [Victory 37.3.6](https://github.com/FormidableLabs/victory/blob/d9d9ca2d5038d6ef9de91f2cef39e6fb2733baa6/LICENSE.txt). The base .NET image carries its own `LICENSE.txt` and `ThirdPartyNotices.txt` under `/usr/share/dotnet`.

The following NuGet packages contribute assemblies to the API publish output. Their upstream notices in this directory come from the repository commits recorded in their NuGet manifests.

| Runtime packages | Upstream sources | Notices |
| --- | --- | --- |
| Dapper 2.1.89 | [DapperLib/Dapper](https://github.com/DapperLib/Dapper/tree/eb47546a408bbf6c178bda4f04dc205bc51ffbfe) | [Dapper](Dapper/License.txt) and the [Apache license text](Dapper/Apache-2.0.txt) it references |
| Microsoft.Extensions.AI.Abstractions 10.11.0 | [dotnet/extensions](https://github.com/dotnet/extensions/tree/25f07afa51034e37b334210642d7dc5f191c142d) | [License](dotnet-extensions/LICENSE) and [third-party notices](dotnet-extensions/THIRD-PARTY-NOTICES.TXT) |
| ModelContextProtocol, ModelContextProtocol.Core, ModelContextProtocol.AspNetCore 2.3.1 | [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk/tree/61420937cbef3eab895cafc8a89418811fb71f54) | [License](mcp-csharp-sdk/LICENSE) and [third-party notices](mcp-csharp-sdk/THIRD-PARTY-NOTICES.txt) |
| Npgsql, Npgsql.DependencyInjection 10.0.3 | [npgsql/npgsql](https://github.com/npgsql/npgsql/tree/d3768398c17877b3a916c3c4d87e8e11698991fc) | [License](Npgsql/LICENSE) |

The Dapper package's copyright field reads “2019 Stack Exchange, Inc.” The MCP packages credit “© Model Context Protocol a Series of LF Projects, LLC.”

When updating a server dependency, compare the package libraries in the published `TokenScope.Api.deps.json` with this table. Check the new package's `.nuspec` for its license, repository commit and copyright, then update the entries and notices that changed. Test packages and SDK build tools are not part of the app image.
