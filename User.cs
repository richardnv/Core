﻿using System;
using Newtonsoft.Json;

namespace Datasilk
{

    public class User
    { 

        [JsonIgnore]
        public Core S;

        public int userId = 0;
        public short userType = 0;
        public string visitorId = "";
        public string email = "";
        public string name = "";
        public string displayName = "";
        public bool photo = false;
        public bool isBot = false;
        public bool useAjax = true;
        public bool isMobile = false;
        public bool isTablet = false;
        public DateTime datecreated;
        public int lastSubjectId = 0;
        public string lastSubjectName = "";

        [JsonIgnore]
        public bool saveSession = false;

        public void Init(Core DatasilkCore)
        {
            S = DatasilkCore;

            //generate visitor id
            if (visitorId == "" || visitorId == null) { visitorId = S.Util.Str.CreateID(); saveSession = true; }
        }

        public virtual void Load()
        { 
        }

        public void LogIn(int userId, string email, string name, DateTime datecreated, string displayname = "", short userType = 1, bool photo = false)
        {
            Load();
            this.userId = userId;
            this.userType = userType;
            this.email = email;
            this.photo = photo;
            this.name = name;
            this.displayName = displayname;
            this.datecreated = datecreated;
            saveSession = true;
        }

        public void LogOut()
        {
            Load();
            userId = 0;
            email = "";
            name = "";
            photo = false;
            saveSession = true;
            S.Session.Remove("user");
        }
    }
}