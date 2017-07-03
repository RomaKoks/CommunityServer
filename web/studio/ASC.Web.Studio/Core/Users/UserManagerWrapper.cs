/*
 *
 * (c) Copyright Ascensio System Limited 2010-2016
 *
 * This program is freeware. You can redistribute it and/or modify it under the terms of the GNU 
 * General Public License (GPL) version 3 as published by the Free Software Foundation (https://www.gnu.org/copyleft/gpl.html). 
 * In accordance with Section 7(a) of the GNU GPL its Section 15 shall be amended to the effect that 
 * Ascensio System SIA expressly excludes the warranty of non-infringement of any third-party rights.
 *
 * THIS PROGRAM IS DISTRIBUTED WITHOUT ANY WARRANTY; WITHOUT EVEN THE IMPLIED WARRANTY OF MERCHANTABILITY OR
 * FITNESS FOR A PARTICULAR PURPOSE. For more details, see GNU GPL at https://www.gnu.org/copyleft/gpl.html
 *
 * You can contact Ascensio System SIA by email at sales@onlyoffice.com
 *
 * The interactive user interfaces in modified source and object code versions of ONLYOFFICE must display 
 * Appropriate Legal Notices, as required under Section 5 of the GNU GPL version 3.
 *
 * Pursuant to Section 7 § 3(b) of the GNU GPL you must retain the original ONLYOFFICE logo which contains 
 * relevant author attributions when distributing the software. If the display of the logo in its graphic 
 * form is not reasonably feasible for technical reasons, you must include the words "Powered by ONLYOFFICE" 
 * in every copy of the program you distribute. 
 * Pursuant to Section 7 § 3(e) we decline to grant you any rights under trademark law for use of our trademarks.
 *
*/


using System;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using ASC.Core;
using ASC.Core.Common.Settings;
using ASC.Core.Tenants;
using ASC.Core.Users;
using ASC.MessagingSystem;
using ASC.Web.Core.Utility;
using ASC.Web.Studio.Core.Notify;
using ASC.Web.Studio.UserControls.Statistics;
using ASC.Web.Studio.Utility;
using Resources;

namespace ASC.Web.Studio.Core.Users
{
    /// <summary>
    /// Web studio user manager helper
    /// </summary>
    public sealed class UserManagerWrapper
    {
        private static bool TestUniqueUserName(string uniqueName)
        {
            if (String.IsNullOrEmpty(uniqueName))
                return false;
            return Equals(CoreContext.UserManager.GetUserByUserName(uniqueName), ASC.Core.Users.Constants.LostUser);
        }

        private static string MakeUniqueName(UserInfo userInfo)
        {
            if (string.IsNullOrEmpty(userInfo.Email))
                throw new ArgumentException(Resource.ErrorEmailEmpty, "userInfo");

            var uniqueName = new MailAddress(userInfo.Email).User;
            var startUniqueName = uniqueName;
            var i = 0;
            while (!TestUniqueUserName(uniqueName))
            {
                uniqueName = string.Format("{0}{1}", startUniqueName, (++i).ToString(CultureInfo.InvariantCulture));
            }
            return uniqueName;
        }

        public static bool CheckUniqueEmail(Guid userId, string email)
        {
            var foundUser = CoreContext.UserManager.GetUserByEmail(email);
            return Equals(foundUser, ASC.Core.Users.Constants.LostUser) || foundUser.ID == userId;
        }

        public static UserInfo AddUser(UserInfo userInfo, string password, bool afterInvite = false, bool notify = true, bool isVisitor = false, bool fromInviteLink = false, bool makeUniqueName = true)
        {
            if (userInfo == null) throw new ArgumentNullException("userInfo");

            CheckPasswordPolicy(password);

            if (!CheckUniqueEmail(userInfo.ID, userInfo.Email))
                throw new Exception(CustomNamingPeople.Substitute<Resource>("ErrorEmailAlreadyExists"));
            if (makeUniqueName)
            {
                userInfo.UserName = MakeUniqueName(userInfo);
            }
            if (!userInfo.WorkFromDate.HasValue)
            {
                userInfo.WorkFromDate = TenantUtil.DateTimeNow();
            }

            if (!CoreContext.Configuration.Personal && !fromInviteLink)
            {
                userInfo.ActivationStatus = !afterInvite ? EmployeeActivationStatus.Pending : EmployeeActivationStatus.Activated;
            }

            var newUserInfo = CoreContext.UserManager.SaveUserInfo(userInfo, isVisitor);
            SecurityContext.SetUserPassword(newUserInfo.ID, password);

            if (CoreContext.Configuration.Personal)
            {
                StudioNotifyService.Instance.SendUserWelcomePersonal(newUserInfo);
                return newUserInfo;
            }

            if ((newUserInfo.Status & EmployeeStatus.Active) == EmployeeStatus.Active && notify)
            {
                //NOTE: Notify user only if it's active
                if (afterInvite)
                {
                    if (isVisitor)
                    {
                        StudioNotifyService.Instance.GuestInfoAddedAfterInvite(newUserInfo);
                    }
                    else
                    {
                        StudioNotifyService.Instance.UserInfoAddedAfterInvite(newUserInfo);
                    }

                    if (fromInviteLink)
                    {
                        StudioNotifyService.Instance.SendEmailActivationInstructions(newUserInfo, newUserInfo.Email);
                    }
                }
                else
                {
                    //Send user invite
                    if (isVisitor)
                    {
                        StudioNotifyService.Instance.GuestInfoActivation(newUserInfo);
                    }
                    else
                    {
                        StudioNotifyService.Instance.UserInfoActivation(newUserInfo);
                    }

                }
            }

            if (isVisitor)
            {
                CoreContext.UserManager.AddUserIntoGroup(newUserInfo.ID, ASC.Core.Users.Constants.GroupVisitor.ID);
            }

            return newUserInfo;
        }

        public static UserInfo AddLDAPUser(UserInfo ldapUserInfo, bool asVisitor)
        {
            var attempt = 3;

            do
            {
                try
                {
                    return AddUser(ldapUserInfo, GeneratePassword(), true, false, asVisitor);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    if (!ex.ParamName.StartsWith("Duplicate username."))
                        throw;

                    attempt--;
                    Thread.Sleep(TimeSpan.FromSeconds(10));

                    if (attempt <= 0)
                        throw;
                }
            } while (attempt > 0);

            throw new ArgumentOutOfRangeException("Duplicate username.");
        }

        public static UserInfo SyncUserLDAP(UserInfo ldapUserInfo)
        {
            UserInfo result = null;

            if(string.IsNullOrEmpty(ldapUserInfo.FirstName))
                ldapUserInfo.FirstName = Resource.FirstName;

            if (string.IsNullOrEmpty(ldapUserInfo.LastName))
                ldapUserInfo.FirstName = Resource.LastName;

            var foundDbUser = SearchExistingUser(ldapUserInfo);

            var asVisitor = TenantStatisticsProvider.GetUsersCount() >=
                                    TenantExtra.GetTenantQuota().ActiveUsers;

            if (Equals(foundDbUser, ASC.Core.Users.Constants.LostUser))
            {
                if (ldapUserInfo.Status != EmployeeStatus.Active)
                    return ASC.Core.Users.Constants.LostUser;

                result = AddLDAPUser(ldapUserInfo, asVisitor);
            }
            else
            {
                if (!NeedUpdateUser(foundDbUser, ldapUserInfo))
                    return foundDbUser;

                // Update info on existing user from LDAP info

                if (!foundDbUser.IsLDAP() || 
                    Equals(foundDbUser.Email, ldapUserInfo.Email) ||
                    Equals(foundDbUser.Sid, ldapUserInfo.Sid))
                {
                    result = UpdateUserWithLDAPInfo(foundDbUser, ldapUserInfo);
                }
                else if (foundDbUser.IsLDAP())
                {
                    if(foundDbUser.IsOwner())
                    {
                        result = UpdateUserWithLDAPInfo(foundDbUser, ldapUserInfo);
                    }
                    else
                    {
                        CoreContext.UserManager.DeleteUser(foundDbUser.ID);

                        result = AddLDAPUser(ldapUserInfo, asVisitor);
                    }
                }
                else if (!Equals(foundDbUser.Email, ldapUserInfo.Email))
                {
                    var userByNewEmail = CoreContext.UserManager.GetUserByEmail(ldapUserInfo.Email);

                    if (Equals(userByNewEmail, ASC.Core.Users.Constants.LostUser))
                    {
                        result = UpdateUserWithLDAPInfo(foundDbUser, ldapUserInfo);
                    }
                    else
                    {
                        if (foundDbUser.IsOwner())
                        {
                            result = UpdateUserWithLDAPInfo(foundDbUser, ldapUserInfo);
                        }
                        else
                        {
                            CoreContext.UserManager.DeleteUser(foundDbUser.ID);

                            result = UpdateUserWithLDAPInfo(userByNewEmail, ldapUserInfo);
                        }
                    }
                }
            }

            return result;
        }

        private static UserInfo SearchExistingUser(UserInfo userInfo)
        {
            var foundUser = CoreContext.UserManager.GetUserBySid(userInfo.Sid);

            if (Equals(foundUser, ASC.Core.Users.Constants.LostUser))
            {
                foundUser = CoreContext.UserManager.GetUserByEmail(userInfo.Email);
            }

            return foundUser;
        }

        private static bool NeedUpdateUser(UserInfo foundUser, UserInfo userInfo)
        {
            var needUpdate = foundUser.FirstName != userInfo.FirstName ||
                             foundUser.LastName != userInfo.LastName ||
                             foundUser.Email != userInfo.Email ||
                             foundUser.Sid != userInfo.Sid ||
                             foundUser.ActivationStatus != userInfo.ActivationStatus ||
                             foundUser.Status != userInfo.Status ||
                             foundUser.Title != userInfo.Title ||
                             foundUser.Location != userInfo.Location ||
                             userInfo.Contacts.Any(c => !foundUser.Contacts.Contains(c));

            return needUpdate;
        }

        private static UserInfo UpdateUserWithLDAPInfo(UserInfo userToUpdate, UserInfo updateInfo)
        {
            userToUpdate.FirstName = updateInfo.FirstName;
            userToUpdate.LastName = updateInfo.LastName;
            userToUpdate.Email = updateInfo.Email;
            userToUpdate.Sid = updateInfo.Sid;
            userToUpdate.ActivationStatus = updateInfo.ActivationStatus;
            userToUpdate.Contacts = updateInfo.Contacts;
            userToUpdate.Title = updateInfo.Title;
            userToUpdate.Location = updateInfo.Location;

            if (!userToUpdate.IsOwner()) // Owner must never be terminated by LDAP!
            {
                userToUpdate.Status = updateInfo.Status;
            }

            return CoreContext.UserManager.SaveUserInfo(userToUpdate);
        }

        #region Password

        public static void CheckPasswordPolicy(string password)
        {
            if (String.IsNullOrEmpty(password))
                throw new Exception(Resource.ErrorPasswordEmpty);

            var passwordSettingsObj = SettingsManager.Instance.LoadSettings<PasswordSettings>(TenantProvider.CurrentTenantID);

            if (!PasswordSettings.CheckPasswordRegex(passwordSettingsObj, password))
                throw new Exception(GenerateErrorMessage(passwordSettingsObj));
        }

        public static UserInfo SendUserPassword(string email)
        {
            email = (email ?? "").Trim();
            if (!email.TestEmailRegex()) throw new ArgumentNullException("email", Resource.ErrorNotCorrectEmail);

            var tenant = CoreContext.TenantManager.GetCurrentTenant();
            var settings = SettingsManager.Instance.LoadSettings<IPRestrictionsSettings>(tenant.TenantId);
            if (settings.Enable && !IPSecurity.IPSecurity.Verify(tenant))
            {
                throw new Exception(Resource.ErrorAccessRestricted);
            }

            var userInfo = CoreContext.UserManager.GetUserByEmail(email);
            if (!CoreContext.UserManager.UserExists(userInfo.ID) || string.IsNullOrEmpty(userInfo.Email))
            {
                throw new Exception(String.Format(Resource.ErrorUserNotFoundByEmail, email));
            }
            if (userInfo.Status == EmployeeStatus.Terminated)
            {
                throw new Exception(Resource.ErrorDisabledProfile);
            }
            if (userInfo.IsLDAP())
            {
                throw new Exception(Resource.CouldNotRecoverPasswordForLdapUser);
            }
            if (userInfo.IsSSO())
            {
                throw new Exception(Resource.CouldNotRecoverPasswordForSsoUser);
            }

            StudioNotifyService.Instance.UserPasswordChange(userInfo);

            var displayUserName = userInfo.DisplayUserName(false);
            MessageService.Send(HttpContext.Current.Request, MessageAction.UserSentPasswordChangeInstructions, displayUserName);

            return userInfo;
        }

        private const string Noise = "1234567890mnbasdflkjqwerpoiqweyuvcxnzhdkqpsdk@%&;";

        public static string GeneratePassword()
        {
            var ps = SettingsManager.Instance.LoadSettings<PasswordSettings>(TenantProvider.CurrentTenantID);

            var maxLength = PasswordSettings.MaxLength
                            - (ps.Digits ? 1 : 0)
                            - (ps.UpperCase ? 1 : 0)
                            - (ps.SpecSymbols ? 1 : 0);
            var minLength = Math.Min(ps.MinLength, maxLength);

            return String.Format("{0}{1}{2}{3}",
                                 GeneratePassword(minLength, minLength, Noise.Substring(0, Noise.Length - 4)),
                                 ps.Digits ? GeneratePassword(1, 1, Noise.Substring(0, 10)) : String.Empty,
                                 ps.UpperCase ? GeneratePassword(1, 1, Noise.Substring(10, 20).ToUpper()) : String.Empty,
                                 ps.SpecSymbols ? GeneratePassword(1, 1, Noise.Substring(Noise.Length - 4, 4).ToUpper()) : String.Empty);
        }

        private static readonly Random Rnd = new Random();

        internal static string GeneratePassword(int minLength, int maxLength, string noise)
        {
            var length = Rnd.Next(minLength, maxLength + 1);

            var pwd = string.Empty;
            while (length-- > 0)
            {
                pwd += noise.Substring(Rnd.Next(noise.Length - 1), 1);
            }
            return pwd;
        }

        internal static string GenerateErrorMessage(PasswordSettings passwordSettings)
        {
            var error = new StringBuilder();

            error.AppendFormat("{0} ", Resource.ErrorPasswordMessage);
            error.AppendFormat(Resource.ErrorPasswordLength, passwordSettings.MinLength, PasswordSettings.MaxLength);
            if (passwordSettings.UpperCase)
                error.AppendFormat(", {0}", Resource.ErrorPasswordNoUpperCase);
            if (passwordSettings.Digits)
                error.AppendFormat(", {0}", Resource.ErrorPasswordNoDigits);
            if (passwordSettings.SpecSymbols)
                error.AppendFormat(", {0}", Resource.ErrorPasswordNoSpecialSymbols);

            return error.ToString();
        }

        public static string GetPasswordHelpMessage()
        {
            var info = new StringBuilder();
            var passwordSettings = SettingsManager.Instance.LoadSettings<PasswordSettings>(TenantProvider.CurrentTenantID);
            info.AppendFormat("{0} ", Resource.ErrorPasswordMessageStart);
            info.AppendFormat(Resource.ErrorPasswordLength, passwordSettings.MinLength, PasswordSettings.MaxLength);
            if (passwordSettings.UpperCase)
                info.AppendFormat(", {0}", Resource.ErrorPasswordNoUpperCase);
            if (passwordSettings.Digits)
                info.AppendFormat(", {0}", Resource.ErrorPasswordNoDigits);
            if (passwordSettings.SpecSymbols)
                info.AppendFormat(", {0}", Resource.ErrorPasswordNoSpecialSymbols);

            return info.ToString();
        }

        #endregion

        public static bool ValidateEmail(string email)
        {
            const string pattern = @"^(([^<>()[\]\\.,;:\s@\""]+"
                                   + @"(\.[^<>()[\]\\.,;:\s@\""]+)*)|(\"".+\""))@((\[[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}"
                                   + @"\.[0-9]{1,3}\])|(([a-zA-Z\-0-9]+\.)+[a-zA-Z]{2,}))$";
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Compiled;
            return new Regex(pattern, options).IsMatch(email);
        }
    }
}