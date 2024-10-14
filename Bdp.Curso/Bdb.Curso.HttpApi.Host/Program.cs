using Bdb.Curso.Application.Shared.Dtos;
using Bdb.Curso.EntityFrameworkCore.Mapping;
using Bdb.Curso.EntityFrameworkCore;
using Bdb.Curso.HttpApi.Host.Services;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Security.Cryptography;
using AspNetCoreRateLimit;
using Bdb.Curso.Infraestructure;
using Bdb.Curso.Application.Validator;
using Azure.Identity;

namespace Bdb.Curso.HttpApi.Host
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configuración de Serilog
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .CreateLogger();

            // Agrega un registro de prueba
            Log.Information("Aplicación iniciada.");


            try
            {
                     

                // Configuración de TwoFactorSettings
                var twoFactorSettings = builder.Configuration.GetSection("TwoFactorAuthentication").Get<TwoFactorSettings>();
                builder.Services.Configure<TwoFactorSettings>(builder.Configuration.GetSection("TwoFactorAuthentication"));


                // Configuración  EmailSettings
                var emailSettings = builder.Configuration.GetSection("EmailSettings").Get<EmailSettings>();
                 builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));

                                   


                //Parte 1 JWE
                // Configuración JWT
                var jwtSettings = builder.Configuration.GetSection("JwtSettingsValues").Get<JwtSettingsValues>();

                if (string.IsNullOrEmpty(jwtSettings.PrivateKeyPath) || string.IsNullOrEmpty(jwtSettings.PublicKeyPath))
                {
                    throw new InvalidOperationException("Las rutas de las claves públicas o privadas no están configuradas.");
                }

                // Registro para compartir la configuracion leida del appsetting
                builder.Services.Configure<JwtSettingsValues>(builder.Configuration.GetSection("JwtSettingsValues"));
                builder.Services.AddSingleton(jwtSettings);

                // Cargar las claves desde archivos
                var privateKeyContent = File.ReadAllText(jwtSettings.PrivateKeyPath);
                //var publicKeyContent = File.ReadAllText(jwtSettings.PublicKeyPath);

                // autenticación JWT

                RSA rsa = RSA.Create();
                rsa.ImportFromPem(privateKeyContent.ToCharArray());

                var rsaSecurityKey = new RsaSecurityKey(rsa);

                builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwtSettings.Issuer,
                        ValidAudience = jwtSettings.Audience,
                        IssuerSigningKey = rsaSecurityKey,
                        ClockSkew = TimeSpan.Zero // Opcional: Elimina el margen de 5 minutos en la expiración de tokens

                    };
                });

                // rate limiting
                builder.Services.AddOptions();
                builder.Services.AddMemoryCache();
                builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
                builder.Services.Configure<IpRateLimitPolicies>(builder.Configuration.GetSection("IpRateLimitPolicies"));
                builder.Services.AddInMemoryRateLimiting();
                builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
                builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();





                // Add services to the container.

                builder.Services.AddControllers(options =>
                {
                    // Desactivar la validación de Data Annotations (para evitar duplicación)
                    options.ModelValidatorProviders.Clear();
                })
                .AddCustomFluentValidation();


                // seccion 1        
                // Agregar NSwag para generar la documentación OpenAPI/Swagger
                builder.Services.AddOpenApiDocument(config =>
                {
                    config.Title = "Curso API BP WA 02";
                    config.Description = "Documentación de acciones";
                    config.Version = "v1";

                    // Definir el esquema de autenticación Bearer JWT      - parte 2
                    config.AddSecurity("bearer", Enumerable.Empty<string>(), new NSwag.OpenApiSecurityScheme
                    {
                        Type = NSwag.OpenApiSecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        In = NSwag.OpenApiSecurityApiKeyLocation.Header,
                        Description = "Ingrese el token JWT en este formato: Bearer {token}"
                    });

                    // Aplicar el esquema de seguridad a todas las operaciones
                    config.OperationProcessors.Add(new NSwag.Generation.Processors.Security.AspNetCoreOperationSecurityScopeProcessor("bearer"));

                });

                //el sitio azure de la boveda
                var keyVaultUrl = builder.Configuration["keyVaultUrl"] ?? string.Empty;

                if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "local")
                {
                    //valores que guardamos en el launch de la aplicacion local
                    string tenantId = Environment.GetEnvironmentVariable("tenantId") ?? string.Empty;
                    string clientId = Environment.GetEnvironmentVariable("clientId") ?? string.Empty;
                    string clientSecret = Environment.GetEnvironmentVariable("clientSecret") ?? string.Empty;

                    ClientSecretCredential tokenCredentials;

                    try
                    {
                        tokenCredentials = new ClientSecretCredential(tenantId, clientId, clientSecret);
                        builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUrl), tokenCredentials);
                    }
                    catch (Exception ee)
                    {
                        throw;
                    }

                }
                else
                {
                    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUrl), new DefaultAzureCredential());
                }

                //pedir el secreto
                var sqlsecret = builder.Configuration["ConnectionStrings-DefaultConnection"] ?? string.Empty;
                                                            
                //Registrar DbContext 
                //builder.Services.AddDbContext<ApplicationDbContext>(options =>
                //    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
                //     .AddInterceptors(new CustomDbCommandInterceptor())
                //     .AddInterceptors(new PerformanceInterceptor())
                //    );

                builder.Services.AddDbContext<ApplicationDbContext>(options =>
                   options.UseSqlServer(sqlsecret)
                    .AddInterceptors(new CustomDbCommandInterceptor())
                    .AddInterceptors(new PerformanceInterceptor())
                   );


                builder.Services
                    .AddApplication();


                //Mapeos autorizados
                builder.Services.AddAutoMapper(typeof(MappingProfile));



                /// builder.Services.AddMemoryCache(); // Configura el servicio de caching
                builder.Services.AddSingleton<CacheService>(); // Registra el servicio de cache


                var app = builder.Build();

                //Middleware de manejo de excepciones
                //app.UseMiddleware<ExceptionHandlingMiddleware>();
                app.UseMiddleware<ExceptionHandlingMiddlewareSL>();


                //seccion 2
                // Middleware para servir la documentación de NSwag
                if (app.Environment.IsDevelopment() || (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "local"))
                {
                    app.UseOpenApi(); // Generar el archivo OpenAPI/Swagger JSON
                    app.UseSwaggerUi(); // Servir Swagger UI con NSwag
                }

                // Configure the HTTP request pipeline.

                app.UseHttpsRedirection();

                // Middleware de Rate Limiting
                 app.UseIpRateLimiting();


                // Usar autenticación y autorización en el middleware
                app.UseAuthentication();

                app.UseAuthorization();


                app.MapControllers();

                app.Run();


            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "La aplicación falló al iniciar.");
            }
            finally
            {
                Log.CloseAndFlush();
            }



        }


    }
}
