﻿using FreeSql;
using Lazy.SlideCaptcha.Core.Validator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Org.BouncyCastle.Utilities.Encoders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using ZhonTai.Admin.Core.Attributes;
using ZhonTai.Admin.Core.Auth;
using ZhonTai.Admin.Core.Captcha;
using ZhonTai.Admin.Core.Configs;
using ZhonTai.Admin.Core.Consts;
using ZhonTai.Admin.Core.Dto;
using ZhonTai.Admin.Core.Helpers;
using ZhonTai.Admin.Domain.Permission;
using ZhonTai.Admin.Domain.PkgPermission;
using ZhonTai.Admin.Domain.RolePermission;
using ZhonTai.Admin.Domain.Tenant;
using ZhonTai.Admin.Domain.TenantPermission;
using ZhonTai.Admin.Domain.TenantPkg;
using ZhonTai.Admin.Domain.User;
using ZhonTai.Admin.Domain.UserRole;
using ZhonTai.Admin.Resources;
using ZhonTai.Admin.Services.Auth.Dto;
using ZhonTai.Admin.Services.LoginLog;
using ZhonTai.Admin.Services.LoginLog.Dto;
using ZhonTai.Admin.Services.User;
using ZhonTai.Common.Extensions;
using ZhonTai.Common.Helpers;
using ZhonTai.DynamicApi;
using ZhonTai.DynamicApi.Attributes;
using static Lazy.SlideCaptcha.Core.ValidateResult;

namespace ZhonTai.Admin.Services.Auth;

/// <summary>
/// 认证授权服务
/// </summary>
[DynamicApi(Area = AdminConsts.AreaName)]
public class AuthService : BaseService, IAuthService, IDynamicApi
{
    
    private readonly Lazy<IOptions<AppConfig>> _appConfig;
    private readonly Lazy<IOptions<JwtConfig>> _jwtConfig;
    private readonly Lazy<IUserRepository> _userRep;
    private readonly Lazy<ITenantRepository> _tenantRep;
    private readonly Lazy<IPermissionRepository> _permissionRep;
    private readonly Lazy<IPasswordHasher<UserEntity>> _passwordHasher;
    private readonly Lazy<ISlideCaptcha> _captcha;
    private readonly UserHelper _userHelper;
    private readonly AdminLocalizer _adminLocalizer;

    public AuthService(
        Lazy<IOptions<AppConfig>> appConfig,
        Lazy<IOptions<JwtConfig>> jwtConfig,
        Lazy<IUserRepository> userRep,
        Lazy<ITenantRepository> tenantRep,
        Lazy<IPermissionRepository> permissionRep,
        Lazy<IPasswordHasher<UserEntity>> passwordHasher,
        Lazy<ISlideCaptcha> captcha,
        UserHelper userHelper,
        AdminLocalizer adminLocalizer
    )
    {
        _appConfig = appConfig;
        _jwtConfig = jwtConfig;
        _userRep = userRep;
        _tenantRep = tenantRep;
        _permissionRep = permissionRep;
        _passwordHasher = passwordHasher;
        _captcha = captcha;
        _userHelper = userHelper;
        _adminLocalizer = adminLocalizer;
    }

    /// <summary>
    /// 获得token
    /// </summary>
    /// <param name="user">用户信息</param>
    /// <returns></returns>
    [NonAction]
    public string GetToken(AuthLoginOutput user)
    {
        if (user == null)
        {
            return string.Empty;
        }

        var claims = new List<Claim>()
       {
            new Claim(ClaimAttributes.UserId, user.Id.ToString(), ClaimValueTypes.Integer64),
            new Claim(ClaimAttributes.UserName, user.UserName),
            new Claim(ClaimAttributes.Name, user.Name),
            new Claim(ClaimAttributes.UserType, user.Type.ToInt().ToString(), ClaimValueTypes.Integer32),
            new Claim(JwtRegisteredClaimNames.Iat, DateTime.Now.ToTimestamp().ToString(), ClaimValueTypes.Integer64),
        };

        if (_appConfig.Value.Value.Tenant)
        {
            claims.AddRange(new []
            {
                new Claim(ClaimAttributes.TenantId, user.TenantId.ToString(), ClaimValueTypes.Integer64),
                new Claim(ClaimAttributes.TenantType, user.Tenant?.TenantType.ToInt().ToString(), ClaimValueTypes.Integer32),
                new Claim(ClaimAttributes.DbKey, user.Tenant?.DbKey ?? "")
            });
        }

        var token = LazyGetRequiredService<IUserToken>().Create(claims.ToArray());

        return token;
    }

    /// <summary>
    /// 查询密钥
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [AllowAnonymous]
    [NoOprationLog]
    public async Task<AuthGetPasswordEncryptKeyOutput> GetPasswordEncryptKeyAsync()
    {
        //写入Redis
        var guid = Guid.NewGuid().ToString("N");
        var key = CacheKeys.PassWordEncrypt + guid;
        //创建key
        byte[] keyBytes = Encoding.Default.GetBytes(StringHelper.GenerateRandom(16));
        string keyHexString = BitConverter.ToString(keyBytes);
        var encryptKey = keyHexString.Replace("-", "").ToLower();
        //创建iv
        byte[] ivBytes = Encoding.Default.GetBytes(StringHelper.GenerateRandom(16));
        string ivHexString = BitConverter.ToString(ivBytes);
        var iv = ivHexString.Replace("-", "").ToLower();
        //输出
        var passwordKeyOutput= new AuthGetPasswordEncryptKeyOutput { Key = guid, EncryptKey = encryptKey, Iv = iv };
        //写缓存
        await Cache.SetAsync(key, passwordKeyOutput, TimeSpan.FromMinutes(5));
        return passwordKeyOutput;
    }

    /// <summary>
    /// 查询用户个人信息
    /// </summary>
    /// <returns></returns>
    [Login]
    public async Task<AuthUserProfileDto> GetUserProfileAsync()
    {
        if (!(User?.Id > 0))
        {
            throw ResultOutput.Exception(_adminLocalizer["未登录"]);
        }

        var userRep = _userRep.Value;
        using var _u = userRep.DataFilter.Disable(FilterNames.Self, FilterNames.Data);

        var profile = await userRep
        .Where(u => u.Id == User.Id)
        .FirstAsync(u => new AuthUserProfileDto 
        {
            DeptName = u.Org.Name,
            CorpName = u.Tenant.Org.Name,
            Position = u.Staff.Position
        });

        var mobile = profile.Mobile?.ToString();
        string number = string.Empty;
        if (mobile.NotNull())
        {
            number = mobile.Length >= 4 ? mobile.Substring(mobile.Length - 4) : mobile;
        }

        if (number.IsNull())
        {
            var userId = User.Id.ToString();
            number = userId.Length >= 4 ? userId.Substring(userId.Length - 4) : userId;
        }

        profile.WatermarkText = $"{profile.Name}@{profile.CorpName} {number}";
            
        return profile;
    }
   
    /// <summary>
    /// 查询用户菜单列表
    /// </summary>
    /// <returns></returns>
    [Login]
    public async Task<List<AuthUserMenuDto>> GetUserMenusAsync()
    {
        if (!(User?.Id > 0))
        {
            throw ResultOutput.Exception(_adminLocalizer["未登录"]);
        }

        using (_userRep.Value.DataFilter.Disable(FilterNames.Self, FilterNames.Data))
        {
            var permissionRep = _permissionRep.Value;
            var menuSelect = permissionRep.Select;

            if (!User.PlatformAdmin)
            {
                var db = permissionRep.Orm;
                if (User.TenantAdmin)
                {
                    menuSelect = menuSelect.Where(a =>
                       db.Select<TenantPermissionEntity>()
                       .Where(b => b.PermissionId == a.Id && b.TenantId == User.TenantId)
                       .Any()
                       ||
                       db.Select<TenantPkgEntity, PkgPermissionEntity>()
                       .Where((b, c) => b.PkgId == c.PkgId && b.TenantId == User.TenantId && c.PermissionId == a.Id)
                       .Any()
                   );
                }
                else
                {
                    menuSelect = menuSelect.Where(a =>
                       db.Select<RolePermissionEntity>()
                       .InnerJoin<UserRoleEntity>((b, c) => b.RoleId == c.RoleId && c.UserId == User.Id)
                       .Where(b => b.PermissionId == a.Id)
                       .Any()
                   );
                }

                menuSelect = menuSelect.AsTreeCte(up: true);
            }

            var menuList = await menuSelect
                .Where(a => new[] { PermissionType.Group, PermissionType.Menu }.Contains(a.Type))
                .ToListAsync(a => new AuthUserMenuDto { ViewPath = a.View.Path });

            return menuList.DistinctBy(a => a.Id).OrderBy(a => a.ParentId).ThenBy(a => a.Sort).ToList();

        }
    }

    /// <summary>
    /// 查询用户权限列表
    /// </summary>
    /// <returns></returns>
    [Login]
    public async Task<AuthGetUserPermissionsOutput> GetUserPermissionsAsync()
    {
        if (!(User?.Id > 0))
        {
            throw ResultOutput.Exception(_adminLocalizer["未登录"]);
        }

        var userRep = _userRep.Value;
        var permissionRep = _permissionRep.Value;

        using (userRep.DataFilter.Disable(FilterNames.Self, FilterNames.Data))
        {
            var authGetUserPermissionsOutput = new AuthGetUserPermissionsOutput();

            var dotSelect = permissionRep.Select.Where(a => a.Type == PermissionType.Dot);

            if (!User.PlatformAdmin)
            {
                var db = permissionRep.Orm;
                if (User.TenantAdmin)
                {
                    dotSelect = dotSelect.Where(a =>
                       db.Select<TenantPermissionEntity>()
                       .Where(b => b.PermissionId == a.Id && b.TenantId == User.TenantId)
                       .Any()
                       ||
                       db.Select<TenantPkgEntity, PkgPermissionEntity>()
                       .Where((b, c) => b.PkgId == c.PkgId && b.TenantId == User.TenantId && c.PermissionId == a.Id)
                       .Any()
                    );
                }
                else
                {
                    dotSelect = dotSelect.Where(a =>
                        db.Select<RolePermissionEntity>()
                        .InnerJoin<UserRoleEntity>((b, c) => b.RoleId == c.RoleId && c.UserId == User.Id)
                        .Where(b => b.PermissionId == a.Id)
                        .Any()
                    );
                }
            }

            //用户权限点
            authGetUserPermissionsOutput.Permissions = await dotSelect.ToListAsync(a => a.Code);

            return authGetUserPermissionsOutput;
        }
    }

    /// <summary>
    /// 查询用户信息
    /// </summary>
    /// <returns></returns>
    [Login]
    public async Task<AuthGetUserInfoOutput> GetUserInfoAsync()
    {
        if (!(User?.Id > 0))
        {
            throw ResultOutput.Exception(_adminLocalizer["未登录"]);
        }

        var userRep = _userRep.Value;
        var permissionRep = _permissionRep.Value;

        using (userRep.DataFilter.Disable(FilterNames.Self, FilterNames.Data))
        {
            var authGetUserInfoOutput = new AuthGetUserInfoOutput
            {
                //用户信息
                User = await userRep.GetAsync<AuthUserProfileDto>(User.Id)
            };

            var menuSelect = permissionRep.Select;
            var dotSelect = permissionRep.Select.Where(a => a.Type == PermissionType.Dot);

            if (!User.PlatformAdmin)
            {
                var db = permissionRep.Orm;
                if (User.TenantAdmin)
                {
                    menuSelect = menuSelect.Where(a =>
                       db.Select<TenantPermissionEntity>()
                       .Where(b => b.PermissionId == a.Id && b.TenantId == User.TenantId)
                       .Any()
                       ||
                       db.Select<TenantPkgEntity, PkgPermissionEntity>()
                       .Where((b, c) => b.PkgId == c.PkgId && b.TenantId == User.TenantId && c.PermissionId == a.Id)
                       .Any()
                   );

                    dotSelect = dotSelect.Where(a =>
                       db.Select<TenantPermissionEntity>()
                       .Where(b => b.PermissionId == a.Id && b.TenantId == User.TenantId)
                       .Any()
                       ||
                       db.Select<TenantPkgEntity, PkgPermissionEntity>()
                       .Where((b, c) => b.PkgId == c.PkgId && b.TenantId == User.TenantId && c.PermissionId == a.Id)
                       .Any()
                    );
                }
                else
                {
                    menuSelect = menuSelect.Where(a =>
                       db.Select<RolePermissionEntity>()
                       .InnerJoin<UserRoleEntity>((b, c) => b.RoleId == c.RoleId && c.UserId == User.Id)
                       .Where(b => b.PermissionId == a.Id)
                       .Any()
                   );

                    dotSelect = dotSelect.Where(a =>
                        db.Select<RolePermissionEntity>()
                        .InnerJoin<UserRoleEntity>((b, c) => b.RoleId == c.RoleId && c.UserId == User.Id)
                        .Where(b => b.PermissionId == a.Id)
                        .Any()
                    );
                }

                menuSelect = menuSelect.AsTreeCte(up: true);
            }

            var menuList = await menuSelect
                .Where(a => new[] { PermissionType.Group, PermissionType.Menu }.Contains(a.Type))
                .ToListAsync(a => new AuthUserMenuDto { ViewPath = a.View.Path });

            //用户菜单
            authGetUserInfoOutput.Menus = menuList.DistinctBy(a => a.Id).OrderBy(a => a.ParentId).ThenBy(a => a.Sort).ToList();

            //用户权限点
            authGetUserInfoOutput.Permissions = await dotSelect.ToListAsync(a => a.Code);

            return authGetUserInfoOutput;
        }
    }

    /// <summary>
    /// 登录
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [HttpPost]
    [AllowAnonymous]
    [NoOprationLog]
    public async Task<dynamic> LoginAsync(AuthLoginInput input)
    {
        var stopwatch = Stopwatch.StartNew();

        #region 验证码校验

        if (_appConfig.Value.Value.VarifyCode.Enable)
        {
            if(input.CaptchaId.IsNull() || input.CaptchaData.IsNull())
            {
                throw ResultOutput.Exception(_adminLocalizer["请完成安全验证"]);
            }
            var validateResult = _captcha.Value.Validate(input.CaptchaId, JsonConvert.DeserializeObject<SlideTrack>(input.CaptchaData));
            if (validateResult.Result != ValidateResultType.Success)
            {
                throw ResultOutput.Exception(_adminLocalizer["安全{0}，请重新登录", validateResult.Message]);
            }
        }

        #endregion

        #region 密码解密

        if (input.PasswordKey.NotNull())
        {
            var passwordEncryptKey = CacheKeys.PassWordEncrypt + input.PasswordKey;
            var existsPasswordKey = await Cache.ExistsAsync(passwordEncryptKey);
            if (existsPasswordKey)
            {
                var secretKey = await Cache.GetAsync<AuthGetPasswordEncryptKeyOutput>(passwordEncryptKey);
                if (secretKey.EncryptKey.IsNull())
                {
                    throw ResultOutput.Exception(_adminLocalizer["解密失败"]);
                }
                input.Password = SM4Encryption.Decrypt(input.Password, Hex.Decode(secretKey.EncryptKey), Hex.Decode(secretKey.Iv), "CBC", true).TrimEnd('\0');//SM4解密后会有\0符号，需要去除。
                await Cache.DelAsync(passwordEncryptKey);
            }
            else
            {
                throw ResultOutput.Exception(_adminLocalizer["解密失败"]);
            }
        }

        #endregion

        #region 登录
        var userRep = _userRep.Value;
        using var _ = userRep.DataFilter.DisableAll();
        using var __ = userRep.DataFilter.Enable(FilterNames.Delete);

        UserEntity user = null;
        user = input.AccountType switch
        {
            AccountType.UserName => await userRep.Select.Where(a => a.UserName == input.UserName).ToOneAsync(),
            AccountType.Mobile => await userRep.Select.Where(a => a.Mobile == input.Mobile).ToOneAsync(),
            AccountType.Email => await userRep.Select.Where(a => a.Email == input.Email).ToOneAsync(),
            _ => null
        };

        var valid = user?.Id > 0;
        if (!valid)
        {
            throw ResultOutput.Exception(_adminLocalizer["账号不存在"]);
        }

        if (valid)
        {
            if (user.PasswordEncryptType == PasswordEncryptType.PasswordHasher)
            {
                var passwordVerificationResult = _passwordHasher.Value.VerifyHashedPassword(user, user.Password, input.Password);
                valid = passwordVerificationResult == PasswordVerificationResult.Success || passwordVerificationResult == PasswordVerificationResult.SuccessRehashNeeded;
            }
            else
            {
                var password = MD5Encrypt.Encrypt32(input.Password);
                valid = user.Password == password;
            }
        }
            
        if(!valid)
        {
            throw ResultOutput.Exception(_adminLocalizer["账号或密码错误"]);
        }

        if (!user.Enabled)
        {
            throw ResultOutput.Exception(_adminLocalizer["账号已停用，禁止登录"]);
        }
        #endregion

        #region 获得token
        var authLoginOutput = Mapper.Map<AuthLoginOutput>(user);
        if (_appConfig.Value.Value.Tenant)
        {
            var tenant = await _tenantRep.Value.Select.WhereDynamic(user.TenantId).ToOneAsync<AuthLoginTenantDto>();
            if (!(tenant != null && tenant.Enabled))
            {
                throw ResultOutput.Exception(_adminLocalizer["企业已停用，禁止登录"]);
            }
            authLoginOutput.Tenant = tenant;
        }
        string token = GetToken(authLoginOutput);
        #endregion

        stopwatch.Stop();

        #region 添加登录日志

        var loginLogAddInput = new LoginLogAddInput
        {
            TenantId = authLoginOutput.TenantId,
            Name = authLoginOutput.Name,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Status = true,
            CreatedUserId = authLoginOutput.Id,
            CreatedUserName = user.UserName,
        };

        await LazyGetRequiredService<ILoginLogService>().AddAsync(loginLogAddInput);

        #endregion 添加登录日志

        return new { token };
    }

    /// <summary>
    /// 手机登录
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [HttpPost]
    [AllowAnonymous]
    [NoOprationLog]
    public async Task<dynamic> MobileLoginAsync(AuthMobileLoginInput input)
    {
        var userRep = _userRep.Value;

        using var _ = userRep.DataFilter.DisableAll();
        using var __ = userRep.DataFilter.Enable(FilterNames.Delete);

        var stopwatch = Stopwatch.StartNew();

        #region 短信验证码验证
        if (input.CodeId.IsNull() || input.Code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        var codeKey = CacheKeys.GetSmsCodeKey(input.Mobile, input.CodeId);
        var code = await Cache.GetAsync(codeKey);
        if (code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        await Cache.DelAsync(codeKey);
        if (code != input.Code)
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }

        #endregion

        #region 登录
        var user = await userRep.Select.Where(a => a.Mobile == input.Mobile).ToOneAsync();
        if (!(user?.Id > 0))
        {
            throw ResultOutput.Exception(_adminLocalizer["账号不存在"]);
        }

        if (!user.Enabled)
        {
            throw ResultOutput.Exception(_adminLocalizer["账号已停用，禁止登录"]);
        }
        #endregion

        #region 获得token
        var authLoginOutput = Mapper.Map<AuthLoginOutput>(user);
        if (_appConfig.Value.Value.Tenant)
        {
            var tenant = await _tenantRep.Value.Select.WhereDynamic(user.TenantId).ToOneAsync<AuthLoginTenantDto>();
            if (!(tenant != null && tenant.Enabled))
            {
                throw ResultOutput.Exception(_adminLocalizer["企业已停用，禁止登录"]);
            }
            authLoginOutput.Tenant = tenant;
        }
        string token = GetToken(authLoginOutput);
        #endregion

        stopwatch.Stop();

        #region 添加登录日志

        var loginLogAddInput = new LoginLogAddInput
        {
            TenantId = authLoginOutput.TenantId,
            Name = authLoginOutput.Name,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Status = true,
            CreatedUserId = authLoginOutput.Id,
            CreatedUserName = user.UserName,
        };

        await LazyGetRequiredService<ILoginLogService>().AddAsync(loginLogAddInput);

        #endregion 添加登录日志

        return new { token };
    }

    /// <summary>
    /// 邮箱登录
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [HttpPost]
    [AllowAnonymous]
    [NoOprationLog]
    public async Task<dynamic> EmailLoginAsync(AuthEmailLoginInput input)
    {
        var userRep = _userRep.Value;

        using var _ = userRep.DataFilter.DisableAll();
        using var __ = userRep.DataFilter.Enable(FilterNames.Delete);

        var stopwatch = Stopwatch.StartNew();

        #region 邮箱验证码验证
        if (input.CodeId.IsNull() || input.Code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        var codeKey = CacheKeys.GetEmailCodeKey(input.Email, input.CodeId);
        var code = await Cache.GetAsync(codeKey);
        if (code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        await Cache.DelAsync(codeKey);
        if (code != input.Code)
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }

        #endregion

        #region 登录
        var user = await userRep.Select.Where(a => a.Email == input.Email).ToOneAsync();
        if (!(user?.Id > 0))
        {
            throw ResultOutput.Exception(_adminLocalizer["账号不存在"]);
        }

        if (!user.Enabled)
        {
            throw ResultOutput.Exception(_adminLocalizer["账号已停用，禁止登录"]);
        }
        #endregion

        #region 获得token
        var authLoginOutput = Mapper.Map<AuthLoginOutput>(user);
        if (_appConfig.Value.Value.Tenant)
        {
            var tenant = await _tenantRep.Value.Select.WhereDynamic(user.TenantId).ToOneAsync<AuthLoginTenantDto>();
            if (!(tenant != null && tenant.Enabled))
            {
                throw ResultOutput.Exception(_adminLocalizer["企业已停用，禁止登录"]);
            }
            authLoginOutput.Tenant = tenant;
        }
        string token = GetToken(authLoginOutput);
        #endregion

        stopwatch.Stop();

        #region 添加登录日志

        var loginLogAddInput = new LoginLogAddInput
        {
            TenantId = authLoginOutput.TenantId,
            Name = authLoginOutput.Name,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Status = true,
            CreatedUserId = authLoginOutput.Id,
            CreatedUserName = user.UserName,
        };

        await LazyGetRequiredService<ILoginLogService>().AddAsync(loginLogAddInput);

        #endregion 添加登录日志

        return new { token };
    }

    /// <summary>
    /// 邮箱更改密码
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [HttpPost]
    [AllowAnonymous]
    [NoOprationLog]
    public async Task ChangePasswordByEmailAsync(AuthChangePasswordByEmailInput input)
    {
        if (input.ConfirmPassword.NotNull() && input.ConfirmPassword != input.NewPassword)
        {
            throw ResultOutput.Exception(_adminLocalizer["新密码和确认密码不一致"]);
        }

        //检查密码格式
        _userHelper.CheckPassword(input.NewPassword);

        #region 邮箱验证码验证

        if (input.Email.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["请输入邮箱地址"]);
        }

        if (input.CodeId.IsNull() || input.Code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        var codeKey = CacheKeys.GetEmailCodeKey(input.Email, input.CodeId);
        var code = await Cache.GetAsync(codeKey);
        if (code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        await Cache.DelAsync(codeKey);
        if (code != input.Code)
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }

        #endregion

        var userRep = _userRep.Value;
        using var _ = userRep.DataFilter.DisableAll();
        using var __ = userRep.DataFilter.Enable(FilterNames.Delete);

        UserEntity user = await userRep.Select.Where(a => a.Email == input.Email).ToOneAsync();

        if (user == null) 
        {
            throw ResultOutput.Exception(_adminLocalizer["账号不存在"]);
        }

        if (user.PasswordEncryptType == PasswordEncryptType.PasswordHasher)
        {
            user.Password = _passwordHasher.Value.HashPassword(user, input.NewPassword);
        }
        else
        {
            user.Password = MD5Encrypt.Encrypt32(input.NewPassword);
        }

        await userRep.UpdateAsync(user);
    }

    /// <summary>
    /// 手机更改密码
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [HttpPost]
    [AllowAnonymous]
    [NoOprationLog]
    public async Task ChangePasswordByMobileAsync(AuthChangePasswordByMobileInput input)
    {
        if (input.ConfirmPassword.NotNull() && input.ConfirmPassword != input.NewPassword)
        {
            throw ResultOutput.Exception(_adminLocalizer["新密码和确认密码不一致"]);
        }

        //检查密码格式
        _userHelper.CheckPassword(input.NewPassword);

        #region 短信验证码验证

        if (input.Mobile.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["请输入手机号"]);
        }

        if (input.CodeId.IsNull() || input.Code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        var codeKey = CacheKeys.GetSmsCodeKey(input.Mobile, input.CodeId);
        var code = await Cache.GetAsync(codeKey);
        if (code.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }
        await Cache.DelAsync(codeKey);
        if (code != input.Code)
        {
            throw ResultOutput.Exception(_adminLocalizer["验证码错误"]);
        }

        #endregion

        var userRep = _userRep.Value;
        using var _ = userRep.DataFilter.DisableAll();
        using var __ = userRep.DataFilter.Enable(FilterNames.Delete);

        UserEntity user = await userRep.Select.Where(a => a.Mobile == input.Mobile).ToOneAsync();

        if (user == null)
        {
            throw ResultOutput.Exception(_adminLocalizer["账号不存在"]);
        }

        if (user.PasswordEncryptType == PasswordEncryptType.PasswordHasher)
        {
            user.Password = _passwordHasher.Value.HashPassword(user, input.NewPassword);
        }
        else
        {
            user.Password = MD5Encrypt.Encrypt32(input.NewPassword);
        }

        await userRep.UpdateAsync(user);
    }

    /// <summary>
    /// 刷新Token
    /// 以旧换新
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    [HttpGet]
    [AllowAnonymous]
    public async Task<dynamic> Refresh([BindRequired] string token)
    {
        var jwtSecurityToken = LazyGetRequiredService<IUserToken>().Decode(token);
        var userClaims = jwtSecurityToken?.Claims?.ToArray();
        if (userClaims == null || userClaims.Length == 0)
        {
            throw ResultOutput.Exception(_adminLocalizer["无法解析token"]);
        }

        var refreshExpires = userClaims.FirstOrDefault(a => a.Type == ClaimAttributes.RefreshExpires)?.Value;
        if (refreshExpires.IsNull() || refreshExpires.ToLong() <= DateTime.Now.ToTimestamp())
        {
            throw ResultOutput.Exception(_adminLocalizer["登录信息已过期"]);
        }

        var userId = userClaims.FirstOrDefault(a => a.Type == ClaimAttributes.UserId)?.Value;
        if (userId.IsNull())
        {
            throw ResultOutput.Exception(_adminLocalizer["登录信息已失效"]);
        }

        //验签
        var securityKey = _jwtConfig.Value.Value.SecurityKey;
        var signingCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(securityKey)), SecurityAlgorithms.HmacSha256);
        var input = jwtSecurityToken.RawHeader + "." + jwtSecurityToken.RawPayload;
        if (jwtSecurityToken.RawSignature != JwtTokenUtilities.CreateEncodedSignature(input, signingCredentials))
        {
            throw ResultOutput.Exception(_adminLocalizer["验签失败"]);
        }

        var user = await LazyGetRequiredService<IUserService>().GetLoginUserAsync(userId.ToLong());
        if(!(user?.Id > 0))
        {
            throw ResultOutput.Exception(_adminLocalizer["账号不存在"]);
        }
        if (!user.Enabled)
        {
            throw ResultOutput.Exception(_adminLocalizer["账号已停用，禁止登录"]);
        }

        if (_appConfig.Value.Value.Tenant)
        {
            if (!(user.Tenant != null && user.Tenant.Enabled))
            {
                throw ResultOutput.Exception(_adminLocalizer["企业已停用，禁止登录"]);
            }
        }

        string newToken = GetToken(user);
        return new { token = newToken };
    }

    /// <summary>
    /// 是否开启验证码
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [AllowAnonymous]
    [NoOprationLog]
    public bool IsCaptcha()
    {
        return _appConfig.Value.Value.VarifyCode.Enable;
    }
}