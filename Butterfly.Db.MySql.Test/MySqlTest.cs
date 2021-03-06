﻿/* Any copyright is dedicated to the Public Domain.
 * http://creativecommons.org/publicdomain/zero/1.0/ */

using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Butterfly.Db.Test;

namespace Butterfly.Db.MySql.Test {
    [TestClass]
    public class MySqlDatabaseUnitTest {
        [TestMethod]
        public async Task TestDatabase() {
            var database = new MySqlDatabase("Server=127.0.0.1;Uid=test;Pwd=test!123;Database=butterfly_db_test");
            await DatabaseUnitTest.TestDatabase(database);
        }

        [TestMethod]
        public async Task TestDynamic() {
            var database = new MySqlDatabase("Server=127.0.0.1;Uid=test;Pwd=test!123;Database=butterfly_db_test");
            await DynamicUnitTest.TestDatabase(database);
        }
    }

}
