﻿using Bdb.Curso.Application.Shared;
using Bdb.Curso.Application.Shared.Dtos;
using Bdb.Curso.Core.Entities;
using Bdb.Curso.EntityFrameworkCore;
using Bdb.Curso.HttpApi.Host.Authorization;
using Jose;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
 

namespace Bdb.Curso.HttpApi.Host.Services
{
                      
    public class JwtTokenService
    {
        private readonly JwtSettingsValues _jwtSettings;

        private readonly TwoFactorSettings _twoFactorSettings;
        private readonly IEmailSenderService _emailSender; // Servicio para enviar correos electrónicos

        private readonly RSA _privateKey;
        private readonly RSA _publicKey;
        private readonly IGenericRepository<RefreshToken> _refreshTokenRepository;
        private readonly IGenericRepository<User> _userRepository;

        public JwtTokenService(
            JwtSettingsValues jwtSettings,
            IOptions<TwoFactorSettings> twoFactorSettings,
            IEmailSenderService emailSender,
            IGenericRepository<RefreshToken> refreshTokenRepository,
            IGenericRepository<User> userRepository)
        {
            _refreshTokenRepository = refreshTokenRepository;
            _jwtSettings = jwtSettings;
            _twoFactorSettings = twoFactorSettings.Value;
            _emailSender = emailSender;


            var privateKeyPem = _jwtSettings.PrivateKeyPath;
            var publicKeyPem = _jwtSettings.PublicKeyPath;

            _privateKey = RSA.Create();

            if (string.IsNullOrWhiteSpace(privateKeyPem))
            {
                throw new InvalidOperationException("La clave privada PEM no puede ser nula o vacía.");
            }

            _privateKey.ImportFromPem(privateKeyPem.ToCharArray());

            _publicKey = RSA.Create();

            if (string.IsNullOrWhiteSpace(publicKeyPem))
            {
                throw new InvalidOperationException("La clave privada PEM no puede ser nula o vacía.");
            }
            _publicKey.ImportFromPem(publicKeyPem.ToCharArray());

            _userRepository = userRepository;
        }

     

                            

        public async Task<TokenResponseDto> GenerateToken(UserDto user, string clientType)
        {
            if (_twoFactorSettings.Enabled)
            {
                // Generar el código de verificación y enviarlo por correo
                          
                byte[] randomNumber = new byte[4];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(randomNumber);
                }
                int verificationCode = BitConverter.ToInt32(randomNumber, 0) % 900000 + 100000; // Gera un número entre 100000 y 999999
                                                                

                user.TwoFactorCode = verificationCode.ToString();
                user.TwoFactorExpiry = DateTime.UtcNow.AddMinutes(10); // El código expira en 10 minutos



                var userDb = await _userRepository.Where(x => x.Id == user.Id).AsNoTracking().FirstOrDefaultAsync();

                if (userDb != null)
                {

                    userDb.TwoFactorCode = user.TwoFactorCode;
                    userDb.TwoFactorExpiry = user.TwoFactorExpiry;

                    try
                    {
                        // Actualizar el usuario en la base de datos
                        await _userRepository.UpdateAsync(userDb);

                    }
                    catch 
                    {

                        throw;
                    }
                }
                else
                {

                    throw new InvalidOperationException("Error enviando el código de verificación 2FA.");

                }


                try
                {
                    // Enviar el código de verificación por correo
                    await _emailSender.SendEmailAsync(
                        user.Email,
                        "Código de verificación",
                        $"Tu código de verificación es: {verificationCode}");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Error enviando el código de verificación 2FA.", ex);
                }

                // Retornar un token temporal hasta que el usuario valide el código de 2FA
                var tokenString = GeneratePending2FAToken(user);

                return new TokenResponseDto
                {
                    AccessToken = tokenString,
                    RefreshToken = new RefreshTokenDto() {Token=string.Empty,Expires=DateTime.Now } // No se genera RefreshToken hasta que se valide 2FA

                };
            }


            else
            {
                // Generar el token directamente
                return await GenerateFullToken(user, clientType);
            }
        }

        private string GeneratePending2FAToken(UserDto user)
        {
            var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName ?? string.Empty),
            new Claim(ClaimTypes.Role, "pending-2fa") // Indica que 2FA está pendiente
        };



            // Agregar los roles como claims y autorizaciones personalizadas
            var rolesList = (user.Roles ?? string.Empty)
                         .Split(',')
                         .Select(r => r.Trim())
                         .ToList();
                                          
       
            foreach (var role in rolesList)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));

                // Obtener permisos desde la clase estática para cada rol
                var permissions = RolePermissionsStore.GetPermissionsByRole(role);
                foreach (var permission in permissions)
                {
                    claims.Add(new Claim("Permission", permission));
                }
            }



            var tokenHandler = new JwtSecurityTokenHandler();
            var key = new RsaSecurityKey(_privateKey);

            //var tokenDescriptor = new SecurityTokenDescriptor
            //{
            //    Subject = new ClaimsIdentity(claims),
            //    Expires = DateTime.UtcNow.AddMinutes(10), // Token temporal
            //    SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            //};


            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpiresInMinutes),
                Issuer = _jwtSettings.Issuer,
                Audience = _jwtSettings.Audience,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            };


            var accessToken = tokenHandler.CreateToken(tokenDescriptor);
            var accessTokenString = tokenHandler.WriteToken(accessToken);

            // Codificar el token usando JWE
            var encryptedToken = JWT.Encode(
                accessTokenString,
                _publicKey,
                JweAlgorithm.RSA_OAEP,
                JweEncryption.A256GCM);

            return encryptedToken;
        }

        public async Task<TokenResponseDto> ValidateTwoFactorAndGenerateToken(UserDto user, string code)
        {
            // Verifica si el código es correcto y no ha expirado
            if (user.TwoFactorCode != code || user.TwoFactorExpiry < DateTime.UtcNow)
            {
                throw new SecurityTokenException("Código 2FA incorrecto o expirado.");
            }

            // Limpiar el código de 2FA una vez validado
            user.TwoFactorCode = null;
            user.TwoFactorExpiry = null;


            // Obtener el usuario actualizado desde la base de datos
            var userFromDb = await _userRepository.GetByIdAsync(user.Id);

            // Actualizar el usuario en la base de datos
            await _userRepository.UpdateAsync(userFromDb);


            // Generar el token completo después de validar 2FA
            return await GenerateFullToken(user, "user");
        }

        private async Task<TokenResponseDto> GenerateFullToken(UserDto user, string clientType)
        {
            var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName??string.Empty),
            new Claim("client_type", clientType)
        };

            


            // Agregar los roles como claims y autorizaciones personalizadas
            var rolesList = (user.Roles ?? string.Empty)
                         .Split(',')
                         .Select(r => r.Trim())
                         .ToList();

                                     
            foreach (var role in rolesList)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));

                // Obtener permisos desde la clase estática para cada rol
                var permissions = RolePermissionsStore.GetPermissionsByRole(role);
                foreach (var permission in permissions)
                {
                    claims.Add(new Claim("Permission", permission));
                }
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = new RsaSecurityKey(_privateKey);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpiresInMinutes),
                Issuer = _jwtSettings.Issuer,
                Audience = _jwtSettings.Audience,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            };

            var accessToken = tokenHandler.CreateToken(tokenDescriptor);
            var accessTokenString = tokenHandler.WriteToken(accessToken);

            var encryptedToken = JWT.Encode(
                accessTokenString,
                _publicKey,
                JweAlgorithm.RSA_OAEP,
                JweEncryption.A256GCM);

            // Generar Refresh Token
            var refreshToken = await GenerateRefreshToken(user.Id);

            return new TokenResponseDto
            {
                AccessToken = encryptedToken,
                RefreshToken = refreshToken
            };
        }

        public string DecryptToken(string encryptedToken)
        {
            try
            {
                var decryptedToken = JWT.Decode(encryptedToken, _privateKey);
                return decryptedToken;
            }
            catch (Exception ex)
            {
                throw new SecurityTokenException("Token inválido o no pudo ser desencriptado", ex);
            }
        }

        // Métodos para obtener el emisor, audiencia y clave pública
        public string GetIssuer()
        {
            return _jwtSettings.Issuer??string.Empty; // Obtener del archivo de configuración
        }

        public string GetAudience()
        {
            return _jwtSettings.Audience ?? string.Empty; // Obtener del archivo de configuración
        }

        public RsaSecurityKey GetPublicKey()
        {
            return new RsaSecurityKey(_publicKey); // Convertir la clave pública a RsaSecurityKey
        }

        public ClaimsPrincipal ValidateToken(string encryptedToken)
        {
            // Desencriptar el token
            var jwt = DecryptToken(encryptedToken);

            // Validar el token desencriptado
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidIssuer = _jwtSettings.Issuer,
                ValidAudience = _jwtSettings.Audience,
                IssuerSigningKey = new RsaSecurityKey(_privateKey) // Clave privada para verificar la firma
            };

            SecurityToken validatedToken;
            var principal = tokenHandler.ValidateToken(jwt, validationParameters, out validatedToken);

            // Verifica si 2FA está habilitado
            if (_twoFactorSettings.Enabled)
            {
                // Busca si el token contiene la información de "pending-2fa"
                var roleClaim = principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value;
                if (roleClaim == "pending-2fa")
                {
                    // Si el usuario aún no ha validado el segundo factor, lanzar excepción
                    throw new SecurityTokenException("Autenticación de doble factor pendiente.");
                }
            }

            // El token es válido y el 2FA está completado (o no está habilitado)
            return principal;
        }

        private async Task<RefreshTokenDto> GenerateRefreshToken(int userId)
        {
            var randomBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }

            var refreshToken = Convert.ToBase64String(randomBytes);

            var rt = new RefreshTokenDto
            {
                Token = refreshToken,
                Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpiresInDays)
            };

            var newRefreshToken = new RefreshToken
            {
                Token = rt.Token,
                Expires = rt.Expires,
                UserId = userId,
                Created = DateTime.UtcNow
            };

            // Guardar en el repositorio
            await _refreshTokenRepository.AddAsync(newRefreshToken);

            return rt;
        }
    }






}