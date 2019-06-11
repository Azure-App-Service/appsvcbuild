using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace appsvcbuild
{
    public class BuildRequest
    {
        [JsonProperty("tries")]
        public int Tries;

        [JsonProperty("stack")]
        public string Stack;

        [JsonProperty("version")]
        public string Version;

        [JsonProperty("pullRepo")]
        public string PullRepo;

        [JsonProperty("pullId")]
        public string PullId;

        [JsonProperty("saveArtifacts")] // default to false
        public Boolean SaveArtifacts;

        [JsonProperty("useCache")] // default to false
        public Boolean UseCache;

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

        [JsonProperty("xdebugTemplateRepoURL")]
        public string XdebugTemplateRepoURL;

        [JsonProperty("xdebugTemplateRepoOrgName")]
        public string XdebugTemplateRepoOrgName;

        [JsonProperty("xdebugTemplateRepoName")]
        public string XdebugTemplateRepoName;

        [JsonProperty("xdebugTemplateName")]
        public string XdebugTemplateName;

        [JsonProperty("xdebugTemplateRepoBranchName")]
        public string XdebugTemplateRepoBranchName;

        [JsonProperty("xdebugBaseImageName")]
        public string XdebugBaseImageName;

        [JsonProperty("xdebugOutputRepoURL")]
        public string XdebugOutputRepoURL;

        [JsonProperty("xdebugOutputRepoName")]
        public string XdebugOutputRepoName;

        [JsonProperty("xdebugOutputRepoOrgName")]
        public string XdebugOutputRepoOrgName;

        [JsonProperty("xdebugOutputRepoBranchName")]
        public string XdebugOutputRepoBranchName;

        [JsonProperty("xdebugOutputImageName")]
        public string XdebugOutputImageName;

        private static String getDotnetcoreTemplate(String version)
        {
            return "debian-9";
        }

        private static String getNodeTemplate(String version)
        {
            return "debian-9";
        }

        private static String getPhpTemplate(String version)
        {
            return "template-apache";
        }

        private static String getPhpXdebugTemplate(String version)
        {
            if (version.StartsWith("7"))
            {
                return "template-php7-apache-xdebug";
            }
            else if (version.StartsWith("5"))
            {
                return "template-php5-apache-xdebug";
            }
            else
            {
                return "";
            }
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
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String randomTag = String.Format("{0}{1}", timeStamp, random);
            if (Tries == 0)
            {
                Tries = 1;
            }

            if (Stack == null)
            {
                throw new Exception("missing stack");
            }
            Stack = Stack.ToLower();
            if (Version == null && !Stack.Equals("kudu"))
            {
                throw new Exception("missing version");
            }
            else if (Stack.Equals("kudu"))
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
                BaseImageName = String.Format("mcr.microsoft.com/oryx/{0}:{1}-latest", Stack, Version);
            }
            if (OutputRepoURL == null)
            {
                if (SaveArtifacts)
                {
                    OutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-{1}.git", Stack, Version);
                }
                else
                {
                    OutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-{1}-{2}.git", Stack, Version, randomTag);
                }
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
                if (SaveArtifacts)
                {
                    OutputImageName = String.Format("{0}:{1}", Stack, Version);
                }
                else
                {
                    OutputImageName = String.Format("{0}:{1}_{2}", Stack, Version, randomTag);
                }
            }
            if (WebAppName == null)
            {
                if (SaveArtifacts)
                {
                    WebAppName = String.Format("appsvcbuild-{0}-hostingstart-{1}-site", Stack, Version.Replace(".", "-"));
                }
                else
                {
                    WebAppName = String.Format("appsvcbuild-{0}-{1}-{2}", Stack, Version.Replace(".", "-"), randomTag);
                }
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
                String[] splitted = TestTemplateRepoURL.Split('/');
                TestTemplateRepoName = splitted[splitted.Length - 1].Replace(".git", ""); ;
            }
            if (TestTemplateRepoOrgName == null)
            {
                String[] splitted = TestTemplateRepoURL.Split('/');
                TestTemplateRepoOrgName = splitted[splitted.Length - 2].Replace(".git", ""); ;
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
                if (SaveArtifacts)
                {
                    TestOutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-app-{1}.git", Stack, Version);
                }
                else
                {
                    TestOutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-app-{1}-{2}.git", Stack, Version, randomTag);
                }
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
                if (SaveArtifacts)
                {
                    TestOutputImageName = String.Format("{0}app:{1}", Stack, Version);
                }
                else
                {
                    TestOutputImageName = String.Format("{0}app:{1}_{2}", Stack, Version, randomTag);
                }
            }
            if (TestWebAppName == null)
            {
                if (SaveArtifacts)
                {
                    TestWebAppName = String.Format("appsvcbuild-{0}-app-{1}-site", Stack, Version.Replace(".", "-"));
                }
                else
                {
                    TestWebAppName = String.Format("appsvcbuild-{0}-app-{1}-{2}", Stack, Version.Replace(".", "-"), randomTag);
                }
            }
            if (XdebugTemplateRepoURL == null)
            {
                XdebugTemplateRepoURL = TemplateRepoURL;
            }
            if (XdebugTemplateRepoName == null)
            {
                String[] splitted = XdebugTemplateRepoURL.Split('/');
                XdebugTemplateRepoName = splitted[splitted.Length - 1].Replace(".git", "");
            }
            if (XdebugTemplateRepoOrgName == null)
            {
                String[] splitted = XdebugTemplateRepoURL.Split('/');
                XdebugTemplateRepoOrgName = splitted[splitted.Length - 2].Replace(".git", "");
            }
            if (XdebugTemplateName == null)
            {
                XdebugTemplateName = getPhpXdebugTemplate(Version);
            }
            if (XdebugTemplateRepoBranchName == null)
            {
                XdebugTemplateRepoBranchName = TemplateRepoBranchName;
            }
            if (XdebugBaseImageName == null)
            {
                XdebugBaseImageName = OutputImageName;
            }
            if (XdebugOutputRepoURL == null)
            {
                if (SaveArtifacts)
                {
                    XdebugOutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-xdebug-{1}.git", Stack, Version);
                }else
                {
                    XdebugOutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-xdebug-{1}-{2}.git", Stack, Version, randomTag);
                }
            }
            if (XdebugOutputRepoName == null)
            {
                String[] splitted = XdebugOutputRepoURL.Split('/');
                XdebugOutputRepoName = splitted[splitted.Length - 1].Replace(".git", "");
            }
            if (XdebugOutputRepoOrgName == null)
            {
                String[] splitted = XdebugOutputRepoURL.Split('/');
                XdebugOutputRepoOrgName = splitted[splitted.Length - 2];
            }
            if (XdebugOutputRepoBranchName == null)
            {
                XdebugOutputRepoBranchName = "master";
            }
            if (XdebugOutputImageName == null)
            {
                if (SaveArtifacts)
                {
                    XdebugOutputImageName = String.Format("{0}xdebug:{1}", Stack, Version);
                }
                else
                {
                    XdebugOutputImageName = String.Format("{0}xdebug:{1}_{2}", Stack, Version, randomTag);

                }
            }
        }
    }
}
