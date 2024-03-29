﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuGet
{
    public class SettingValue
    {
        public SettingValue(string key, string value, bool isMachineWide)
        {
            Key = key;
            Value = value;
            IsMachineWide = isMachineWide;
        }

        public string Key { get; private set; }

        public string Value { get; private set; }

        public bool IsMachineWide { get; private set; }
    }
}
