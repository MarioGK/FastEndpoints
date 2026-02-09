var bld = WebApplication.CreateBuilder(args);
bld.Services
   .SwaggerDocument()
   .AddFastEndpoints();

var app = bld.Build();
app.UseFastEndpoints();
app.UseSwaggerGen();
app.Run();