﻿using System;
using System.Collections.Generic;
using System.Data.SqlServerCe;
using System.Reflection;
using ErikEJ.SqlCeScripting;

namespace ErikEJ.SqlCeToolbox.Helpers
{
    public class DataConnectionHelper
    {
        public static string Argument { get; set; }
        internal static SortedDictionary<string, string> GetDataConnections()
        {
                SortedDictionary<string, string> databaseList = new SortedDictionary<string, string>();
                using (var conn = new SqlCeConnection(CreateStore()))
                {
                    conn.Open();
                    using (var cmd = new SqlCeCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = "SELECT FileName, Source FROM Databases";
                        var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            string key = System.IO.Path.GetFileName(rdr[0].ToString());
                            int x = 0;
                            if (databaseList.ContainsKey(key))
                            {
                                while (databaseList.ContainsKey(key))
                                {
                                    x++;
                                    key = string.Format("{0} ({1})", key, x.ToString());
                                }
                            }
                            databaseList.Add(key, rdr[1].ToString());
                        }
                    }
                }
                return databaseList;
        }

        internal static void SaveDataConnection(string connectionString)
        {
            using (var testConn = new SqlCeConnection(connectionString))
            {
                string filePath = testConn.Database;
                using (var conn = new SqlCeConnection(CreateStore()))
                {
                    conn.Open();
                    using (var cmd = new SqlCeCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = "INSERT INTO Databases (Source, FileName) VALUES (@Source, @FileName)";
                        cmd.Parameters.Add("@Source", System.Data.SqlDbType.NVarChar, 2048);
                        cmd.Parameters.Add("@FileName", System.Data.SqlDbType.NVarChar, 512);

                        cmd.Parameters[0].Value = connectionString;
                        cmd.Parameters[1].Value = filePath;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        internal static void RemoveDataConnection(string connectionString)
        {
            using (var conn = new SqlCeConnection(CreateStore()))
            {
                conn.Open();
                using (var cmd = new SqlCeCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM Databases WHERE Source = @Source;";
                    cmd.Parameters.Add("@Source", System.Data.SqlDbType.NVarChar, 2048);
                    cmd.Parameters[0].Value = connectionString;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static string CreateStore()
        {
            string fileName = GetSdfName();
            string connString = string.Format("Data Source={0};", fileName);
            bool created = false;
            if (!System.IO.File.Exists(fileName))
            {
                using (var engine = new SqlCeEngine(connString))
                {
                    engine.CreateDatabase();
                    created = true;
                }
            }
            using (var conn = new SqlCeConnection(connString))
            {
                if (created)
                {
                    conn.Open();
                    using (var cmd = new SqlCeCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = "CREATE TABLE Databases (Id INT IDENTITY, Source nvarchar(2048) NOT NULL, FileName nvarchar(512) NOT NULL)";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            return connString;
        }

        private static string GetSdfName()
        {
#if V35
            string fileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlCe35ToolboxExe.sdf");
#else
            string fileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlCe40ToolboxExe.sdf");
#endif
            return fileName;
        }

        internal static string ShowErrors(Exception ex)
        {
#if V35
            ISqlCeHelper sqlCeHelper = new SqlCeHelper();
#else
            ISqlCeHelper sqlCeHelper = new SqlCeHelper4();
#endif
            Telemetry.TrackException(ex);
            return sqlCeHelper.FormatError(ex);
        }

        public static bool CheckVersion(string lookingFor)
        {
            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    wc.Proxy = System.Net.WebRequest.GetSystemWebProxy();
                    var xDoc = new System.Xml.XmlDocument();
                    string s = wc.DownloadString(@"http://www.sqlcompact.dk/SqlCeToolboxVersions.xml");
                    xDoc.LoadXml(s);
                    if (xDoc.DocumentElement != null)
                    {
                        string newVersion = xDoc.DocumentElement.Attributes[lookingFor].Value;

                        Version vN = new Version(newVersion);
                        if (vN > Assembly.GetExecutingAssembly().GetName().Version)
                        {
                            return true;
                        }
                    }
                }

            }
            catch
            {
                // ignored
            }
            return false;
        }

        public static bool IsRuntimeInstalled()
        {
#if V35
            return IsV35Installed();
#else
            return IsV40Installed();
#endif
        }

        private static bool IsV40Installed()
        {
            try
            {
                var assembly = Assembly.Load("System.Data.SqlServerCe, Version=4.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91");
                if (assembly.GetName().Version.ToString(2) != "4.0")
                    return false;
            }
            catch
            {
                try
                {
                    var assembly = Assembly.Load("System.Data.SqlServerCe, Version=4.0.0.1, Culture=neutral, PublicKeyToken=89845dcd8080cc91");
                    if (assembly.GetName().Version.ToString(2) != "4.0")
                        return false;
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsV35Installed()
        {
            try
            {
                var assembly = Assembly.Load("System.Data.SqlServerCe, Version=3.5.1.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91");
                if (assembly.GetName().Version.ToString(2) != "3.5")
                    return false;
            }
            catch
            {
                return false;
            }
            return true;
        }
    }
}
