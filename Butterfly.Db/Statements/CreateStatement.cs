﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Butterfly.Db {
    /// <summary>
    /// Internal class used to parse CREATE statements
    /// </summary>
    public class CreateStatement : BaseStatement {
        protected readonly static Regex STATEMENT_REGEX = new Regex(@"CREATE\s+TABLE\s+(?<tableName>\w+)\s*\(\s*(?<createTableDefs>[\s\S]*)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        protected readonly static Regex FIELD_REGEX = new Regex(@"^\s*(?<fieldName>\w+)\s+(?<fieldType>[\w\(\)]+)(?<not>\s*NOT)?(?<null>\s*NULL)?(?<autoIncrement>\s*(AUTO_INCREMENT|.*IDENTITY))?\s*[,$]", RegexOptions.IgnoreCase);
        protected readonly static Regex PRIMARY_KEY_REGEX = new Regex(@"^\s*PRIMARY\s+KEY\s*\((?<fields>[^\)]*)\)\s*(?:,|$)", RegexOptions.IgnoreCase);
        protected readonly static Regex INDEX_REGEX = new Regex(@"^\s*(?<unique>UNIQUE\s+)?(INDEX|KEY)?\s*(?<name>\w+)\s*\((?<fields>[^\)]*)\)\s*(?:,|$)", RegexOptions.IgnoreCase);

        public CreateStatement(string sql) {
            Match match = STATEMENT_REGEX.Match(sql.Trim());
            if (!match.Success) throw new Exception($"Invalid sql '{this.Sql}'");

            this.Sql = sql;

            // Extract field names
            this.TableName = match.Groups["tableName"].Value;

            string createTableDefs = match.Groups["createTableDefs"].Value.Replace("\r", " ").Replace("\n", " ");

            List<TableIndex> indexes = new List<TableIndex>();
            List<TableFieldDef> fieldDefs = new List<TableFieldDef>();
            int lastIndex = 0;
            while (lastIndex<createTableDefs.Length) {
                string substring = createTableDefs.Substring(lastIndex);
                Match primaryKeyMatch = PRIMARY_KEY_REGEX.Match(substring);
                if (primaryKeyMatch.Success) {
                    string[] keyFieldNames = primaryKeyMatch.Groups["fields"].Value.Split(',').Select(x => x.Trim()).ToArray();
                    indexes.Add(new TableIndex(TableIndexType.Primary, keyFieldNames));
                    lastIndex += primaryKeyMatch.Length;
                }
                else {
                    Match indexMatch = INDEX_REGEX.Match(substring);
                    if (indexMatch.Success) {
                        string[] keyFieldNames = indexMatch.Groups["fields"].Value.Split(',').Select(x => x.Trim()).ToArray();
                        indexes.Add(new TableIndex(string.IsNullOrEmpty(indexMatch.Groups["unique"].Value) ? TableIndexType.Other : TableIndexType.Unique, keyFieldNames));
                        lastIndex += indexMatch.Length;
                    }
                    else {
                        Match fieldMatch = FIELD_REGEX.Match(substring);
                        if (fieldMatch.Success) {
                            string fieldName = fieldMatch.Groups["fieldName"].Value;
                            string fieldTypeText = fieldMatch.Groups["fieldType"].Value;
                            (Type fieldType, int maxLength) = ConvertMySqlType(fieldTypeText);
                            bool notNull = fieldMatch.Groups["not"].Success && fieldMatch.Groups["null"].Success;
                            bool autoIncrement = fieldMatch.Groups["autoIncrement"].Success;
                            fieldDefs.Add(new TableFieldDef(fieldName, fieldType, maxLength, !notNull, autoIncrement));
                            lastIndex += fieldMatch.Length;
                        }
                        else {
                            throw new Exception($"Could not parse '{substring}' in {this.Sql}");
                        }
                    }
                }
            }
            this.FieldDefs = fieldDefs.ToArray();
            this.Indexes = indexes.ToArray();
        }

        public string TableName {
            get;
            protected set;
        }

        public TableFieldDef[] FieldDefs {
            get;
            protected set;
        }
        
        public TableIndex[] Indexes {
            get;
            protected set;
        }

        protected readonly static Regex PARSE_TYPE = new Regex(@"^(?<type>.+?)(?<maxLengthWithParens>\(\d+\))?$");

        public static (Type, int) ConvertMySqlType(string text) {
            Match match = PARSE_TYPE.Match(text);
            if (!match.Success) throw new Exception($"Could not parse SQL type '{text}'");

            string typeText = match.Groups["type"].Value;

            Type type;
            if (typeText.EndsWith("CHAR", StringComparison.OrdinalIgnoreCase) || typeText.EndsWith("TEXT", StringComparison.OrdinalIgnoreCase) || typeText.EndsWith("JSON", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(string);
            }
            else if (typeText.Equals("TINYINT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(byte);
            }
            else if (typeText.Equals("MEDIUMINT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(int);
            }
            else if (typeText.Equals("INT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(long);
            }
            else if (typeText.Equals("BIGINT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(long);
            }
            else if (typeText.Equals("FLOAT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(float);
            }
            else if (typeText.Equals("DOUBLE", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(double);
            }
            else if ((typeText.Equals("DATETIME", StringComparison.OrdinalIgnoreCase)) ||
                                         (typeText.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase))) {
                type = typeof(DateTime);
            }
            else {
                throw new Exception($"Unknown field type '{text}'");
            }

            string maxLengthText = match.Groups["maxLengthWithParens"].Value.Replace("(", "").Replace(")", "");
            if (!int.TryParse(maxLengthText, out int maxLength)) maxLength = -1;

            return (type, maxLength);
        }

    }
}
