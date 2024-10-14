﻿using Bdb.Curso.Application.Shared.Dtos;
using Bdb.Curso.Core.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Bdb.Curso.Application.Shared
{
    public interface IUserAppServices
    {
        // crear usuario
        // actualizar
        // login
        // logout
        // etc

        Task<UserDTO> Login(LoginModel login);

         
        Task<UserDTO> CreateUser(CreateUserInput input);

        Task<bool> UpdateUser(int id, CreateUserInput input);

        Task<UserDTO> GetUserByUsername(string username);



    }
}
