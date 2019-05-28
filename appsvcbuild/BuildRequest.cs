using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace appsvcbuild
{
    public class BuildRequest
    {
        [JsonProperty("stack")]
        public string Stack;

        [JsonProperty("version")]
        public string Version;

        [JsonProperty("templateRepoURL")]
        public string TemplateRepoURL;

        [JsonProperty("templateRepoOrgName")]
        public string TemplateRepoOrgName;

        [JsonProperty("templateRepoName")]
        public string TemplateRepoName;

        [JsonProperty("templateName")]
        public string TemplateName;

        [JsonProperty("templateRepoBranchName")]
        public string TemplateRepoBranchName;

        [JsonProperty("baseImageName")]
        public string BaseImageName;

        [JsonProperty("outputRepoURL")]
        public string OutputRepoURL;

        [JsonProperty("outputRepoName")]
        public string OutputRepoName;

        [JsonProperty("outputRepoOrgName")]
        public string OutputRepoOrgName;

        [JsonProperty("outputRepoBranchName")]
        public string OutputRepoBranchName;

        [JsonProperty("outputImageName")]
        public string OutputImageName;

        [JsonProperty("webAppName")]
        public string WebAppName;

        [JsonProperty("email")]
        public string Email;

        [JsonProperty("testTemplateRepoURL")]
        public string TestTemplateRepoURL;

        [JsonProperty("testTemplateRepoOrgName")]
        public string TestTemplateRepoOrgName;

        [JsonProperty("testTemplateRepoName")]
        public string TestTemplateRepoName;

        [JsonProperty("tesTemplateName")]
        public string TestTemplateName;

        [JsonProperty("testTemplateRepoBranchName")]
        public string TestTemplateRepoBranchName;

        [JsonProperty("testBaseImageName")]
        public string TestBaseImageName;

        [JsonProperty("testOutputRepoURL")]
        public string TestOutputRepoURL;

        [JsonProperty("testOutputRepoName")]
        public string TestOutputRepoName;

        [JsonProperty("testOutputRepoOrgName")]
        public string TestOutputRepoOrgName;

        [JsonProperty("testOutputRepoBranchName")]
        public string TestOutputRepoBranchName;

        [JsonProperty("testOutputImageName")]
        public string TestOutputImageName;

        [JsonProperty("testWebAppName")]
        public string TestWebAppName;

        private static String getDotnetcoreTemplate(String version)
        {
            if (version.StartsWith("1"))
            {
                return "debian-8";
            }
            else
            {
                return "debian-9";
            }
        }

        private static String getNodeTemplate(String version)
        {
            return "debian-9";
        }

        private static String getPhpTemplate(String version)
        {

            if (new List<String> { "5.6", "7.0", "7.2", "7.3" }.Contains(version))
            {
                return String.Format("template-{0}-apache", version);
            }

            throw new Exception(String.Format("unexpected php version: {0}", version));
        }

        private static String getPythonTemplate(String version)
        {
            if (new List<String> { "2.7", "3.6", "3.7" }.Contains(version))
            {
                return String.Format("template-{0}", version);
            }

            throw new Exception(String.Format("unexpected python version: {0}", version));
        }

        private static String getRubyTemplate(String version)
        {
            return "template";
        }

        private static String getTemplate(String stack, String version)
        {
            if (stack == "dotnetcore")
            {
                return getDotnetcoreTemplate(version);
            }
            if (stack == "node")
            {
                return getNodeTemplate(version);
            }
            if (stack == "php")
            {
                return getPhpTemplate(version);
            }
            if (stack == "python")
            {
                return getPythonTemplate(version);
            }
            if (stack == "ruby")
            {
                return getRubyTemplate(version);
            }
            if (stack == "kudu")
            {
                return "kudu";
            }

            throw new Exception(String.Format("unexpected stack: {0}", stack));
        }

        private static String getTestTemplate(String stack, String version)
        {
            if (stack == "dotnetcore")
            {
                return "TestAppTemplate";
            }
            if (stack == "node")
            {
                return "nodeAppTemplate";
            }
            if (stack == "php")
            {
                return "template-app-apache";
            }
            if (stack == "python")
            {
                return "template-app";
            }
            if (stack == "ruby")
            {
                return "TestAppTemplate";
            }
            if (stack == "kudu")
            {
                return "kudu";
            }

            throw new Exception(String.Format("unexpected stack: {0}", stack));
        }

        public void processAddDefaults()
        {
            if (Stack == null)
            {
                throw new Exception("missing stack");
            }
            Stack = Stack.ToLower();
            if (Version == null && !Stack.Equals("kudu"))
            {
                throw new Exception("missing version");
            } else if(Stack.Equals("kudu"))
            {
                Version = "0";
            }
            if (TemplateRepoURL == null)
            {
                TemplateRepoURL = String.Format("https://github.com/Azure-App-Service/{0}-template.git", Stack);
            }
            if (TemplateRepoName == null)
            {
                String[] splitted = TemplateRepoURL.Split('/');
                TemplateRepoName = splitted[splitted.Length - 1].Replace(".git", "");
            }
            if (TemplateRepoOrgName == null)
            {
                String[] splitted = TemplateRepoURL.Split('/');
                TemplateRepoOrgName = splitted[splitted.Length - 2];
            }
            if (TemplateName == null)
            {
                TemplateName = getTemplate(Stack, Version);
            }
            if (TemplateRepoBranchName == null)
            {
                TemplateRepoBranchName = "master";
            }
            if (BaseImageName == null)
            {
                BaseImageName = String.Format("mcr.microsoft.com/oryx/{0}-{1}:latest", Stack, Version);
            }
            if (OutputRepoURL == null)
            {
                OutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-{1}.git", Stack, Version);
            }
            if (OutputRepoName == null)
            {
                String[] splitted = OutputRepoURL.Split('/');
                OutputRepoName = splitted[splitted.Length - 1].Replace(".git", "");
            }
            if (OutputRepoOrgName == null)
            {
                String[] splitted = OutputRepoURL.Split('/');
                OutputRepoOrgName = splitted[splitted.Length - 2];
            }
            if (OutputRepoBranchName == null)
            {
                OutputRepoBranchName = "master";
            }
            if (OutputImageName == null)
            {
                OutputImageName = String.Format("{0}:{1}", Stack, Version);
            }
            if (WebAppName == null)
            {
                WebAppName = String.Format("appsvcbuild-{0}-hostingstart-{1}-site", Stack, Version.Replace(".", "-"));
            }
            if (Email == null)
            {
                Email = "patle@microsoft.com";
            }
            if (TestTemplateRepoURL == null)
            {
                TestTemplateRepoURL = TemplateRepoURL;
            }
            if (TestTemplateRepoName == null)
            {
                TestTemplateRepoName = TemplateRepoName;
            }
            if (TestTemplateRepoOrgName == null)
            {
                TestTemplateRepoOrgName = TemplateRepoOrgName;
            }
            if (TestTemplateName == null)
            {
                TestTemplateName = getTestTemplate(Stack, Version);
            }
            if (TestTemplateRepoBranchName == null)
            {
                TestTemplateRepoBranchName = TemplateRepoBranchName;
            }
            if (TestBaseImageName == null)
            {
                TestBaseImageName = OutputImageName;
            }
            if (TestOutputRepoURL == null)
            {
                TestOutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-app-{1}.git", Stack, Version);
            }
            if (TestOutputRepoName == null)
            {
                String[] splitted = TestOutputRepoURL.Split('/');
                TestOutputRepoName = splitted[splitted.Length - 1].Replace(".git", "");
            }
            if (TestOutputRepoOrgName == null)
            {
                String[] splitted = TestOutputRepoURL.Split('/');
                TestOutputRepoOrgName = splitted[splitted.Length - 2];
            }
            if (TestOutputRepoBranchName == null)
            {
                TestOutputRepoBranchName = "master";
            }
            if (TestOutputImageName == null)
            {
                TestOutputImageName = String.Format("{0}app:{1}", Stack, Version);
            }
            if (TestWebAppName == null)
            {
                TestWebAppName = String.Format("appsvcbuild-{0}-app-{1}-site", Stack, Version.Replace(".", "-"));
            }
        }
    }
}
