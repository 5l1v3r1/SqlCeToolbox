﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Microsoft.SqlServer.Dac.Extensions.Prototype;
using Microsoft.SqlServer.Dac.Model;

namespace ReverseEngineer20
{
    public class SqlServerDacpacDatabaseModelFactory : IDatabaseModelFactory
    {
        private static readonly ISet<string> DateTimePrecisionTypes = new HashSet<string> { "datetimeoffset", "datetime2", "time" };

        private static readonly ISet<string> MaxLengthRequiredTypes
            = new HashSet<string> { "binary", "varbinary", "char", "varchar", "nchar", "nvarchar" };

        private readonly IDiagnosticsLogger<DbLoggerCategory.Scaffolding> _logger;

        public SqlServerDacpacDatabaseModelFactory(IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            _logger = logger;
        }

        public virtual DatabaseModel Create(string dacpacPath, IEnumerable<string> tables, IEnumerable<string> schemas)
        {
            if (string.IsNullOrEmpty(dacpacPath))
            {
                throw new ArgumentException(@"invalid path", nameof(dacpacPath));
            }
            if (!File.Exists(dacpacPath))
            {
                throw new ArgumentException("Dacpac file not found");
            }

            var dbModel = new DatabaseModel
            {
                DatabaseName = Path.GetFileNameWithoutExtension(dacpacPath),
                DefaultSchema = schemas.Count() > 0 ? schemas.First() : "dbo"                
            };
            //Sequences not created - not needed for scaffolding

            var model = new TSqlTypedModel(dacpacPath);

            var typeAliases = GetTypeAliases(model, dbModel);

            var items = model.GetObjects<TSqlTable>(DacQueryScopes.UserDefined)
                .Where(t => t.PrimaryKeyConstraints.Any()
                    && !t.GetProperty<bool>(Table.IsAutoGeneratedHistoryTable))
                .Where(t => tables == null || tables.Contains($"{t.Name.Parts[0]}.{t.Name.Parts[1]}"))
                .Where(t => $"{t.Name.Parts[1]}" != HistoryRepository.DefaultTableName)
                .ToList();

            foreach (var item in items)
            {
                var dbTable = new DatabaseTable
                {
                    Name = item.Name.Parts[1],
                    Schema = item.Name.Parts[0],
                };

                if (item.MemoryOptimized)
                {
                    dbTable["SqlServer:MemoryOptimized"] = true;
                }

                GetColumns(item, dbTable, typeAliases, model.GetObjects<TSqlDefaultConstraint>(DacQueryScopes.UserDefined).ToList());
                GetPrimaryKey(item, dbTable);

                dbModel.Tables.Add(dbTable);
            }

            foreach (var item in items)
            {
                GetForeignKeys(item, dbModel);
                GetUniqueConstraints(item, dbModel);
                GetIndexes(item, dbModel);
            }

            return dbModel;
        }

        public DatabaseModel Create(DbConnection connection, IEnumerable<string> tables, IEnumerable<string> schemas) 
            => throw new NotImplementedException();

        private IReadOnlyDictionary<string, (string, string)> GetTypeAliases(TSqlTypedModel model, DatabaseModel dbModel)
        {
            var items = model.GetObjects<TSqlDataType>(DacQueryScopes.UserDefined)
                .ToList();

            var typeAliasMap = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

            foreach (var udt in items)
            {
                int maxLength = udt.UddtIsMax ? -1 : udt.UddtLength;
                var storeType = GetStoreType(udt.Type.First().Name.Parts[0], maxLength, udt.UddtPrecision, udt.UddtScale);
                typeAliasMap.Add($"{udt.Name.Parts[0]}.{udt.Name.Parts[1]}", (storeType, udt.Type.First().Name.Parts[0]));
            }

            return typeAliasMap;
        }

        private void GetPrimaryKey(TSqlTable table, DatabaseTable dbTable)
        {
            var pk = table.PrimaryKeyConstraints.First();
            var primaryKey = new DatabasePrimaryKey
            {
                Name = pk.Name.HasName ? pk.Name.Parts[1] : null,
                Table = dbTable
            };

            if (!pk.Clustered)
            {
                primaryKey["SqlServer:Clustered"] = false;
            }

            foreach (var pkCol in pk.Columns)
            {
                var dbCol = dbTable.Columns
                    .Single(c => c.Name == pkCol.Name.Parts[2]);

                primaryKey.Columns.Add(dbCol);
            }

            dbTable.PrimaryKey = primaryKey;
        }

        private void GetForeignKeys(TSqlTable table, DatabaseModel dbModel)
        {
            var dbTable = dbModel.Tables
                .Single(t => t.Name == table.Name.Parts[1]
                && t.Schema == table.Name.Parts[0]);

            var fks = table.ForeignKeyConstraints.ToList();
            foreach (var fk in fks)
            {
                var foreignTable = dbModel.Tables
                    .SingleOrDefault(t => t.Name == fk.ForeignTable.First().Name.Parts[1]
                    && t.Schema == fk.ForeignTable.First().Name.Parts[0]);

                if (foreignTable == null) continue;

                var foreignKey = new DatabaseForeignKey
                {
                    Name = fk.Name.HasName ? fk.Name.Parts[1] : null,
                    Table = dbTable,
                    PrincipalTable = foreignTable,
                    OnDelete = ConvertToReferentialAction(fk.DeleteAction)
                };

                foreach (var fkCol in fk.Columns)
                {
                    var dbCol = dbTable.Columns
                        .Single(c => c.Name == fkCol.Name.Parts[2]);

                    foreignKey.Columns.Add(dbCol);
                }

                foreach (var fkCol in fk.ForeignColumns)
                {
                    var dbCol = foreignTable.Columns
                        .Single(c => c.Name == fkCol.Name.Parts[2]);

                    foreignKey.PrincipalColumns.Add(dbCol);
                }

                dbTable.ForeignKeys.Add(foreignKey);
            }
        }

        private void GetUniqueConstraints(TSqlTable table, DatabaseModel dbModel)
        {
            var dbTable = dbModel.Tables
                .Single(t => t.Name == table.Name.Parts[1]
                && t.Schema == table.Name.Parts[0]);

            var uqs = table.UniqueConstraints.ToList();
            foreach (var uq in uqs)
            {
                var uniqueConstraint = new DatabaseUniqueConstraint
                {
                    Name = uq.Name.HasName ? uq.Name.Parts[1] : null,
                    Table = dbTable
                };

                if (uq.Clustered)
                {
                    uniqueConstraint["SqlServer:Clustered"] = true;
                }

                foreach (var uqCol in uq.Columns)
                {
                    var dbCol = dbTable.Columns
                        .Single(c => c.Name == uqCol.Name.Parts[2]);

                    uniqueConstraint.Columns.Add(dbCol);
                }

                dbTable.UniqueConstraints.Add(uniqueConstraint);
            }
        }

        private void GetIndexes(TSqlTable table, DatabaseModel dbModel)
        {
            var dbTable = dbModel.Tables
                .Single(t => t.Name == table.Name.Parts[1]
                && t.Schema == table.Name.Parts[0]);

            var ixs = table.Indexes.ToList();
            foreach (var sqlIx in ixs)
            {
                var ix = sqlIx as TSqlIndex;

                if (sqlIx == null) continue;

                var index = new DatabaseIndex
                {
                    Name = ix.Name.Parts[2],
                    Table = dbTable,
                    IsUnique = ix.Unique,
                    Filter = ix.FilterPredicate
                };

                if (ix.Clustered)
                {
                    index["SqlServer:Clustered"] = true;
                }
                foreach (var column in ix.Columns)
                {
                    var dbCol = dbTable.Columns
                        .SingleOrDefault(c => c.Name == column.Name.Parts[2]);

                    if (dbCol != null)
                    {
                        index.Columns.Add(dbCol);
                    }
                }

                if (index.Columns.Count > 0)
                {
                    dbTable.Indexes.Add(index);
                }
            }
        }

        private void GetColumns(TSqlTable item, DatabaseTable dbTable, IReadOnlyDictionary<string, (string storeType, string typeName)> typeAliases, List<TSqlDefaultConstraint> defaultConstraints)
        {
            var tableColumns = item.Columns
                .Where(i => !i.GetProperty<bool>(Column.IsHidden)
                && i.ColumnType != ColumnType.ColumnSet
                // Computed columns not supported for now
                // Probably not possible: https://stackoverflow.com/questions/27259640/get-datatype-of-computed-column-from-dacpac
                && i.ColumnType != ColumnType.ComputedColumn 
                );

            foreach (var col in tableColumns)
            {
                var def = defaultConstraints.Where(d => d.TargetColumn.First().Name.ToString() == col.Name.ToString()).FirstOrDefault();
                string storeType = null;
                string underlyingStoreType = null;
                string systemTypeName = null;

                if (col.DataType.First().Name.Parts.Count > 1)
                {
                    if (typeAliases.TryGetValue($"{col.DataType.First().Name.Parts[0]}.{col.DataType.First().Name.Parts[1]}", out var value))
                    {
                        underlyingStoreType = value.storeType;
                        storeType = col.DataType.First().Name.Parts[1];
                        systemTypeName = value.typeName;
                    }
                }
                else
                {
                    var dataTypeName = col.DataType.First().Name.Parts[0];
                    int maxLength = col.IsMax ? -1 : col.Length;
                    storeType = GetStoreType(dataTypeName, maxLength, col.Precision, col.Scale);
                    underlyingStoreType = null;
                    systemTypeName = dataTypeName;
                }

                string defaultValue = def != null ? FilterClrDefaults(systemTypeName, col.Nullable, def.Expression.ToLowerInvariant()) : null;

                var dbColumn = new DatabaseColumn
                {
                    Table = dbTable,
                    Name = col.Name.Parts[2],
                    IsNullable = col.Nullable,
                    StoreType = storeType,
                    DefaultValueSql = defaultValue,
                    ComputedColumnSql = col.Expression,
                    ValueGenerated = null
                };
                if (col.IsIdentity)
                {
                    dbColumn.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd;
                }
                if ((underlyingStoreType ?? storeType) == "rowversion")
                {
                    dbColumn.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate;
                    dbColumn["ConcurrencyToken"] = true;
                }

                dbColumn.SetUnderlyingStoreType(underlyingStoreType);

                dbTable.Columns.Add(dbColumn);
            }
        }

        private static string GetStoreType(string dataTypeName, int maxLength, int precision, int scale)
        {
            if (dataTypeName == "timestamp")
            {
                return "rowversion";
            }

            if (dataTypeName == "decimal"
                || dataTypeName == "numeric")
            {
                return $"{dataTypeName}({precision}, {scale})";
            }

            if (DateTimePrecisionTypes.Contains(dataTypeName)
                && scale != 7)
            {
                return $"{dataTypeName}({scale})";
            }

            if (MaxLengthRequiredTypes.Contains(dataTypeName))
            {
                if (maxLength == -1)
                {
                    return $"{dataTypeName}(max)";
                }

                return $"{dataTypeName}({maxLength})";
            }

            return dataTypeName;
        }

        private static string FilterClrDefaults(string dataTypeName, bool nullable, string defaultValue)
        {
            defaultValue = StripParentheses(defaultValue);

            if (defaultValue == null
                || defaultValue == "null")
            {
                return null;
            }
            if (nullable)
            {
                return defaultValue;
            }

            if (defaultValue == "0")
            {
                if (dataTypeName == "bigint"
                    || dataTypeName == "bit"
                    || dataTypeName == "decimal"
                    || dataTypeName == "float"
                    || dataTypeName == "int"
                    || dataTypeName == "money"
                    || dataTypeName == "numeric"
                    || dataTypeName == "real"
                    || dataTypeName == "smallint"
                    || dataTypeName == "smallmoney"
                    || dataTypeName == "tinyint")
                {
                    return null;
                }
            }
            else if (defaultValue == "0.0")
            {
                if (dataTypeName == "decimal"
                    || dataTypeName == "float"
                    || dataTypeName == "money"
                    || dataTypeName == "numeric"
                    || dataTypeName == "real"
                    || dataTypeName == "smallmoney")
                {
                    return null;
                }
            }
            else if ((defaultValue == "CONVERT([real],(0))" && dataTypeName == "real")
                || (defaultValue == "0.0000000000000000e+000" && dataTypeName == "float")
                || (defaultValue == "'0001-01-01'" && dataTypeName == "date")
                || (defaultValue == "'1900-01-01T00:00:00.000'" && (dataTypeName == "datetime" || dataTypeName == "smalldatetime"))
                || (defaultValue == "'0001-01-01T00:00:00.000'" && dataTypeName == "datetime2")
                || (defaultValue == "'0001-01-01T00:00:00.000+00:00'" && dataTypeName == "datetimeoffset")
                || (defaultValue == "'00:00:00'" && dataTypeName == "time")
                || (defaultValue == "'00000000-0000-0000-0000-000000000000'" && dataTypeName == "uniqueidentifier"))
            {
                return null;
            }

            return defaultValue;
        }

        private static string StripParentheses(string defaultValue)
        {
            if (defaultValue.StartsWith("(") && defaultValue.EndsWith(")"))
            {
                defaultValue = defaultValue.Substring(1, defaultValue.Length - 2);
                return StripParentheses(defaultValue);
            }
            return defaultValue;
        }

        private static ReferentialAction? ConvertToReferentialAction(ForeignKeyAction onDeleteAction)
        {
            switch (onDeleteAction)
            {
                case ForeignKeyAction.NoAction:
                    return ReferentialAction.NoAction;

                case ForeignKeyAction.Cascade:
                    return ReferentialAction.Cascade;

                case ForeignKeyAction.SetNull:
                    return ReferentialAction.SetNull;

                case ForeignKeyAction.SetDefault:
                    return ReferentialAction.SetDefault;

                default:
                    return null;
            }
        }
    }
}