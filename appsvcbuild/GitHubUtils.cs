using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using LibGit2Sharp;
using System.IO;
using LibGit2Sharp.Handlers;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Rest.Azure;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.Storage;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.ContainerRegistry.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.KeyVault;
using System.Xml.Linq;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace appsvcbuild
{
    public class GitHubUtils
    {
        public ILogger _log { get; set; }
        private String _gitToken;

        public GitHubUtils(String gitToken)
        {
            _gitToken = gitToken;
        }

        public async void InitGithubAsync(String name)
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("patricklee2");
            String url = String.Format("https://api.github.com/user/repos?access_token={0}", _gitToken);
            String body = "{ \"name\": " + JsonConvert.SerializeObject(name) + " }";
            StringContent content = new StringContent(body);

            HttpResponseMessage response = await client.PostAsync(url, new StringContent(body));
            String result = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.Created)
            {
                _log.LogInformation(String.Format("created repo {0}", name));
            }
            else
            {
                //TODO add retyry logic
                //throw new Exception(String.Format("unable to create github repo {0}", name));
                _log.LogInformation(String.Format("unable to create github repo {0}", name));
            }
        }

        public void Init(String dir)
        {
            Repository.Init(dir);
        }

        public void AddRemote(String dir, String repoName)
        {
            Repository repo = new Repository(dir);
            Remote remote = repo.Network.Remotes.Add("origin", String.Format("https://github.com/patricklee2/{0}.git", repoName));
            repo.Branches.Update(repo.Head, b => b.Remote = remote.Name, b => b.UpstreamBranch = repo.Head.CanonicalName);
        }

        public void DeepCopy(String source, String target)
        {
            DeepCopy(new DirectoryInfo(source), new DirectoryInfo(target));
        }

        public void DeepCopy(DirectoryInfo source, DirectoryInfo target)
        {
            target.Create();
            // Recursively call the DeepCopy Method for each Directory
            foreach (DirectoryInfo dir in source.GetDirectories())
                DeepCopy(dir, target.CreateSubdirectory(dir.Name));

            // Go ahead and copy each file in "source" to the "target" directory
            foreach (FileInfo file in source.GetFiles())
                file.CopyTo(Path.Combine(target.FullName, file.Name));
        }

        public void LineChanger(string newText, string fileName, int lineToEdit)
        {
            string[] arrLine = File.ReadAllLines(fileName);
            arrLine[lineToEdit - 1] = newText;
            File.WriteAllLines(fileName, arrLine);
        }

        public void CreateDir(String path)
        {
            //_log.Info("create directory " + path);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public void CleanUp(String path)
        {
            System.Threading.Thread.Sleep(2000);  //milliseconds
            //_log.Info("delete folder " + path);
            if (Directory.Exists(path))
            {
                DeleteRecursiveFolder(new DirectoryInfo(path)); // dont know how to delete files in functions, probably need a file/blob share
            }
        }

        private void DeleteRecursiveFolder(DirectoryInfo dirInfo)
        {
            foreach (DirectoryInfo subDir in dirInfo.GetDirectories())
            {
                DeleteRecursiveFolder(subDir);
            }

            foreach (FileInfo file in dirInfo.GetFiles())
            {
                file.Attributes = FileAttributes.Normal;
                file.Delete();
            }

            dirInfo.Delete();
        }

        public void Clone(String githubURL, String dest)
        {
            //_log.Info("cloning " + githubURL + " to " + dest);
            Repository.Clone(githubURL, dest, new CloneOptions { BranchName = "master" });
        }

        public void FillTemplate(String localRepo, String template, String dest, String dockerFile, String newLine, bool force)
        {
            if (force)
            {
                CleanUp(dest);
            }

            if (Directory.Exists(dest))
            {
                //_log.Info(dest + " already exist");
                return;
            }

            //_log.Info("deep copying");
            // copy template to node_version
            DirectoryInfo source = new DirectoryInfo(template);
            DirectoryInfo target = new DirectoryInfo(dest);
            DeepCopy(source, target);

            // edit dockerfile
            //_log.Info("editing dockerfile");
            LineChanger(newLine, dockerFile, 1);
        }

        public void Stage(String localRepo, String path) {
            // git add
            //_log.Info("git add");
            Commands.Stage(new Repository(localRepo), path);
        }

        public void CommitAndPush(String gitPath, String message)
        {
            try
            {
                using (Repository repo = new Repository(gitPath))
                {
                    // git commit
                    // Create the committer's signature and commit
                    //_log.Info("git commit");
                    Signature author = new Signature("appsvcbuild", "patle@microsoft.com", DateTime.Now);
                    Signature committer = author;

                    // Commit to the repository
                    try
                    {
                        Commit commit = repo.Commit(message, author, committer);
                    }
                    catch (Exception e)
                    {
                        //_log.info("Empty commit");
                    }

                    // git push
                    //_log.Info("git push");
                    LibGit2Sharp.PushOptions options = new LibGit2Sharp.PushOptions();
                    options.CredentialsProvider = new CredentialsHandler(
                        (url, usernameFromUrl, types) =>
                            new UsernamePasswordCredentials()
                            {
                                Username = _gitToken,
                                Password = String.Empty
                            });
                    repo.Network.Push(repo.Branches["master"], options);
                }
            }
            catch (Exception e)
            {
                //TODO better retry logic
                _log.LogInformation(e.ToString());
            }
        }
    }
}
