﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Threading.Tasks;

using RazorEngine;
using RazorEngine.Configuration;
using RazorEngine.Templating;
using RazorEngine.Text;

using ReportUnit.Model;
using ReportUnit.Utils;
using ReportUnit.Logging;

namespace ReportUnit.Parser
{
    internal class NUnit : IParser
    {
        private string resultsFile;

        private Logger logger = Logger.GetLogger();

        public Report Parse(string resultsFile)
        {
            this.resultsFile = resultsFile;

            XDocument doc = XDocument.Load(resultsFile);

            Report report = new Report();

            report.FileName = Path.GetFileNameWithoutExtension(resultsFile);
            report.AssemblyName = doc.Root.Attribute( "name" ) != null ? doc.Root.Attribute("name").Value : null;
            report.TestRunner = TestRunner.NUnit;

            // run-info & environment values -> RunInfo
            var runInfo = CreateRunInfo(doc, report);
            if (runInfo != null) 
            { 
                report.AddRunInfo(runInfo.Info); 
            }

            // report counts
            report.Total = doc.Descendants("test-case").Count();

            report.Passed = 
                doc.Root.Attribute("passed") != null 
                    ? Int32.Parse(doc.Root.Attribute("passed").Value) 
                    : doc.Descendants("test-case").Where(x => x.Attribute("result").Value.Equals("success", StringComparison.CurrentCultureIgnoreCase)).Count();

            report.Failed = 
                doc.Root.Attribute("failed") != null 
                    ? Int32.Parse(doc.Root.Attribute("failed").Value) 
                    : Int32.Parse(doc.Root.Attribute("failures").Value);
            
            report.Errors = 
                doc.Root.Attribute("errors") != null 
                    ? Int32.Parse(doc.Root.Attribute("errors").Value) 
                    : 0;
            
            report.Inconclusive = 
                doc.Root.Attribute("inconclusive") != null 
                    ? Int32.Parse(doc.Root.Attribute("inconclusive").Value) 
                    : Int32.Parse(doc.Root.Attribute("inconclusive").Value);
            
            report.Skipped = 
                doc.Root.Attribute("skipped") != null 
                    ? Int32.Parse(doc.Root.Attribute("skipped").Value) 
                    : Int32.Parse(doc.Root.Attribute("skipped").Value);
            
            report.Skipped += 
                doc.Root.Attribute("ignored") != null 
                    ? Int32.Parse(doc.Root.Attribute("ignored").Value) 
                    : 0;

            // report duration
            report.StartTime = 
                doc.Root.Attribute("start-time") != null 
                    ? doc.Root.Attribute("start-time").Value 
                    : doc.Root.Attribute("date").Value + " " + doc.Root.Attribute("time").Value;

            report.EndTime = 
                doc.Root.Attribute("end-time") != null 
                    ? doc.Root.Attribute("end-time").Value 
                    : "";

            // report status messages
            var testSuiteTypeAssembly = doc.Descendants("test-suite")
                .Where(x => x.Attribute("result").Value.Equals("Failed") && x.Attribute("type").Value.Equals("Assembly"));
            report.StatusMessage = testSuiteTypeAssembly != null && testSuiteTypeAssembly.Count() > 0
                ? testSuiteTypeAssembly.First().Value
                : "";

            IEnumerable<XElement> suites = doc
                .Descendants("test-suite")
                .Where(x => x.Attribute("type").Value.Equals("TestFixture", StringComparison.CurrentCultureIgnoreCase));
            
            suites.AsParallel().ToList().ForEach(ts =>
            {
                var testSuite = new TestSuite();
                testSuite.Name = ts.Attribute("name").Value;

                // Suite Time Info
                testSuite.StartTime = 
                    ts.Attribute("start-time") != null 
                        ? ts.Attribute("start-time").Value 
                        : string.Empty;

                testSuite.StartTime = 
                    String.IsNullOrEmpty(testSuite.StartTime) && ts.Attribute("time") != null 
                        ? ts.Attribute("time").Value 
                        : testSuite.StartTime; 

                testSuite.EndTime = 
                    ts.Attribute("end-time") != null 
                        ? ts.Attribute("end-time").Value 
                        : "";

                // any error messages and/or stack-trace
                var failure = ts.Element("failure");
                if (failure != null)
                {
                    var message = failure.Element("message");
                    if (message != null)
                    {
                        testSuite.StatusMessage = message.Value;
                    }

                    var stackTrace = failure.Element("stack-trace");
                    if (stackTrace != null && !string.IsNullOrWhiteSpace(stackTrace.Value))
                    {
                        testSuite.StatusMessage = string.Format(
                            "{0}\n\nStack trace:\n{1}", testSuite.StatusMessage, stackTrace.Value);
                    }
                }

                // get test suite level categories
                var suiteCategories = this.GetCategories(ts);

                // Test Cases
                ts.Descendants("test-case").AsParallel().ToList().ForEach(tc =>
                {
                    var test = new Model.Test();
                    test.MethodName = tc.Attribute("methodname").Value;
                    test.Name = tc.Attribute("name").Value;
                    test.Status = StatusExtensions.ToStatus(tc.Attribute("result").Value);
                    
                    // main a master list of all status
                    // used to build the status filter in the view
                    report.StatusList.Add(test.Status);

                    // TestCase Time Info
                    test.StartTime = 
                        tc.Attribute("start-time") != null 
                            ? tc.Attribute("start-time").Value 
                            : "";
                    test.StartTime = 
                        String.IsNullOrEmpty(test.StartTime) && (tc.Attribute("time") != null) 
                            ? tc.Attribute("time").Value 
                            : test.StartTime;
                    test.EndTime = 
                        tc.Attribute("end-time") != null 
                            ? tc.Attribute("end-time").Value 
                            : "";
                    //duration
                    string duration = tc.Attribute("duration") != null ? tc.Attribute("duration").Value : "";
                    if (!string.IsNullOrEmpty(duration))
                    {
                        TimeSpan t = TimeSpan.FromSeconds(Convert.ToDouble(duration));
                        test.Duration = t.ToString(@"hh\:mm\:ss\:fff");
                    }
                    
                    // description
                    var description = 
                        tc.Descendants("property")
                        .Where(c => c.Attribute("name").Value.Equals("Description", StringComparison.CurrentCultureIgnoreCase));
                    test.Description = 
                        description.Count() > 0 
                            ? description.ToArray()[0].Attribute("value").Value 
                            : "";

                    // get test case level categories
                    var categories = this.GetCategories(tc);

                    // if this is a parameterized test, get the categories from the parent test-suite
                    var parameterizedTestElement = tc
                        .Ancestors("test-suite").ToList()
                        .Where(x => x.Attribute("type").Value.Equals("ParameterizedTest", StringComparison.CurrentCultureIgnoreCase))
                        .FirstOrDefault();

                    if (null != parameterizedTestElement)
                    {
                        var paramCategories = this.GetCategories(parameterizedTestElement);
                        categories.UnionWith(paramCategories);
                    }

                    //Merge test level categories with suite level categories and add to test and report
                    categories.UnionWith(suiteCategories);
                    test.CategoryList.AddRange(categories);
                    report.CategoryList.AddRange(categories);

                    string delimeter = Environment.NewLine + "====================================================" + Environment.NewLine;
                    // error and other status messages
                    test.StatusMessage = 
                        tc.Element("failure") != null
                            ? delimeter + "EXCEPTION MESSAGE: " + Environment.NewLine + tc.Element("failure").Element("message").Value.Trim()
                            : "";
                    test.StatusMessage += 
                        tc.Element("failure") != null 
                            ? tc.Element("failure").Element("stack-trace") != null 
                                ? delimeter + "EXCEPTION STACKTRACE:" + Environment.NewLine + tc.Element("failure").Element("stack-trace").Value.Trim()
                                : "" 
                            : "";

                    test.StatusMessage += tc.Element("reason") != null && tc.Element("reason").Element("message") != null
                        ? tc.Element("reason").Element("message").Value.Trim()
                        : "";

                   // add NUnit console output to the status message
                   test.StatusMessage += tc.Element( "output" ) != null
                     ? delimeter + "EXECUTE STEPS:" + Environment.NewLine + tc.Element("output").Value.Trim() + delimeter
                     : "";

                   //add screenshot links
                    if (tc.Element("output") != null)
                    {
                        MatchCollection matches = Regex.Matches(tc.Element("output").Value.Trim(),
                            @"Generated Screenshot:\s(<a.*a>)");
                        foreach (Match match in matches)
                        {
                            if (match.Success)
                            {
                                test.ScreenshotLinks.Add(match.Groups[1].Value);
                            }
                        }
                    }
                    testSuite.TestList.Add(test);
                });

                testSuite.Status = ReportUtil.GetFixtureStatus(testSuite.TestList);

                report.TestSuiteList.Add(testSuite);
            });

            
            report.TestSuiteList = report.TestSuiteList.OrderBy(ts => ts.Name).ToList();

            //Sort category list so it's in alphabetical order
            report.CategoryList.Sort();

            return report;
        }

        /// <summary>
        /// Returns categories for the direct children or all descendents of an XElement
        /// </summary>
        /// <param name="elem">XElement to parse</param>
        /// <param name="allDescendents">If true, return all descendent categories.  If false, only direct children</param>
        /// <returns></returns>
        private HashSet<string> GetCategories(XElement elem)
        {
            //Grab unique categories
            HashSet<string> categories = new HashSet<string>();

            var propertiesElement = elem.Elements("properties").ToList();
            if (!propertiesElement.Any())
            {
                return categories;
            }
            //get all <property name="Category"> elements
            var categoryProperties = propertiesElement.Elements("property")
                .Where(c =>
                {
                    var xAttribute = c.Attribute("name");
                    return xAttribute != null && xAttribute.Value.Equals("Category", StringComparison.CurrentCultureIgnoreCase);
                })
                .ToList().ToList();
            if (!categoryProperties.Any())
            {
                return categories;
            }
            categoryProperties.ForEach(x =>
            {
                var xAttribute = x.Attribute("value");
                if (xAttribute != null)
                {
                    string cat = xAttribute.Value;
                    categories.Add(cat);
                }
            });
            return categories;
        }

        private RunInfo CreateRunInfo(XDocument doc, Report report)
        {
            RunInfo runInfo = new RunInfo();
            if (doc.Descendants("test-run").Any())
            {
                XElement testRun = doc.Descendants("test-run").First();
                if (testRun.Attribute("start-time") != null)
                    runInfo.Info.Add("Start time", testRun.Attribute("start-time").Value);

                if (testRun.Attribute("end-time") != null)
                    runInfo.Info.Add("End time", testRun.Attribute("end-time").Value);

                if (testRun.Attribute("duration") != null)
                {
                    string durationAsString = testRun.Attribute("duration").Value;
                    double seconds = Convert.ToDouble(durationAsString);
                    TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
                    string time = timeSpan.ToString(@"hh\h\:mm\m\:ss\s\:fff\m\s");

                    runInfo.Info.Add("Duration", time);
                }
            }

            if (!doc.Descendants("environment").Any())
                return null;

            runInfo.TestRunner = report.TestRunner;

            XElement env = doc.Descendants("environment").First();

            if (env.Attribute("app-under-test") != null)
                runInfo.Info.Add("App under test", env.Attribute("app-under-test").Value);

            if (env.Attribute("app-version") != null)
                runInfo.Info.Add("App version", env.Attribute("app-version").Value);

            if (env.Attribute("app-branch") != null)
                runInfo.Info.Add("App branch", env.Attribute("app-branch").Value);

            if (env.Attribute("syncplicity-full-log") != null)
            {
                var htmlLink = string.Format("<a href='{0}'>syncplicity.log</a>", env.Attribute("syncplicity-full-log").Value);
                runInfo.Info.Add("App full log", htmlLink);
            }

            if (env.Attribute("syncplicity-logs-archive") != null)
            {
                var htmlLink = string.Format("<a href='{0}'>syncplicity_logs.zip</a>", env.Attribute("syncplicity-logs-archive").Value);
                runInfo.Info.Add("App logs archive", htmlLink);
            }

            if (env.Attribute("tests-branch") != null)
                runInfo.Info.Add("Tests branch", env.Attribute("tests-branch").Value);

            if (env.Attribute("environment") != null)
                runInfo.Info.Add("Environment", env.Attribute("environment").Value);

            if (env.Attribute("os-version") != null)
                runInfo.Info.Add("OS Version", env.Attribute("os-version").Value);

            if (env.Attribute("os-architecture") != null)
                runInfo.Info.Add("OS Architecture", env.Attribute("os-architecture").Value);

            if (env.Attribute("machine-name") != null)
                runInfo.Info.Add("Machine Name", env.Attribute("machine-name").Value);


            return runInfo;
        }
        
        public NUnit() { }
    }
}
