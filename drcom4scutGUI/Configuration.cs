using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace drcom4scutGUI
{
    [DataContract]
    public class Account : IComparable<Account>
    {
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; }

        [DataMember(Name = "password", Order = 1)]
        public string Password { get; set; }

        [DataMember(Name = "ip", Order = 2)]
        public string IP { get; set; }

        public int CompareTo(Account other)
        {
            return string.Compare(this.Name, other.Name);
        }

    }

    [DataContract]
    public class Configuration
    {
        public static readonly string FilePath = Path.ChangeExtension(
            System.Reflection.Assembly.GetExecutingAssembly().Location, ".json");

        private static readonly DataContractJsonSerializer serializer = new(typeof(Configuration));

        [DataMember(Name = "accounts", Order = 0)]
        public List<Account> Accounts { get; set; } = [];

        [DataMember(Name = "mac", Order = 1)]
        public string Mac { get; set; } = "";

        [DataMember(Name = "account", Order = 2)]
        public string Account { get; set; } = "";

        [DataMember(Name = "auto", Order = 3)]
        public bool Auto { get; set; } = false;

        public Account Find(string name)
        {
            return Accounts.Find(account => account.Name == name);
        }

        private static Configuration Normalize(Configuration config)
        {
            config.Accounts ??= [];
            config.Accounts.RemoveAll(account => account == null || string.IsNullOrEmpty(account.Name));
            foreach (Account account in config.Accounts)
            {
                account.IP ??= "";
                account.Password ??= "";
            }
            config.Mac ??= "";
            return config;
        }

        public static Configuration Load()
        {
            if (!File.Exists(FilePath)) return new Configuration();
            using MemoryStream stream = new(File.ReadAllBytes(FilePath));
            return Normalize((Configuration)serializer.ReadObject(stream) ?? new Configuration());
        }

        public void Save()
        {
            using MemoryStream stream = new();
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, System.Text.Encoding.UTF8, false, true))
            {
                serializer.WriteObject(writer, this);
            }
            File.WriteAllBytes(FilePath, stream.ToArray());
        }
    }
}
