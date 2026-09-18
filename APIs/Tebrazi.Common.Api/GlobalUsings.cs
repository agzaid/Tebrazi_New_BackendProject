// This project uses the class-library SDK with a FrameworkReference to ASP.NET Core, so it does
// not get the Web SDK's implicit usings. Declaring them once here keeps every file in the
// project reading the same as one inside an API host.
global using Microsoft.AspNetCore.Http;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
