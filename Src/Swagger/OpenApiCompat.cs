// Conditional global usings for Microsoft.OpenApi namespace differences
// .NET 9 uses Microsoft.OpenApi 1.x with types in Microsoft.OpenApi.Models
// .NET 10 uses Microsoft.OpenApi 2.x with types in Microsoft.OpenApi root namespace

#if NET10_0_OR_GREATER
global using Microsoft.OpenApi;
#else
global using Microsoft.OpenApi.Models;
#endif
