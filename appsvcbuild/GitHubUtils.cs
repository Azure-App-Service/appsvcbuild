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

        public async Task<bool> RepoExistsAsync(String orgName, String repoName)
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("patricklee2");
            String url = String.Format("https://api.github.com/repos/{0}/{1}", orgName, repoName);
            
            HttpResponseMessage response = await client.GetAsync(url);
            
            if(response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            return true;
        }

        public async Task<Boolean> InitGithubAsync(String orgName, String repoName)
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("patricklee2");
            String url = String.Format("https://api.github.com/orgs/{0}/repos?access_token={1}", orgName, _gitToken);
            String body = "{ \"name\": " + JsonConvert.SerializeObject(repoName) + " }";

            HttpResponseMessage response = await client.PostAsync(url, new StringContent(body));
            String result = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.Created)
            {
                _log.LogInformation(String.Format("created repo {0}/{1}", orgName, repoName));
            }
            else
            {
                //TODO add retyry logic
                //throw new Exception(String.Format("unable to create github repo {0}/{1}", orgName, repoName));
                _log.LogInformation(String.Format("unable to create github  repo {0}/{1}", orgName, repoName));
            }
            return true;    //return when done
        }

        public void Init(String dir)
        {
            Repository.Init(dir);
        }

        public void AddRemote(String dir, String orgName, String repoName)
        {
            Repository repo = new Repository(dir);
            Remote remote = repo.Network.Remotes.Add("origin", String.Format("https://github.com/{0}/{1}.git", orgName, repoName));
            repo.Branches.Update(repo.Head, b => b.Remote = remote.Name, b => b.UpstreamBranch = repo.Head.CanonicalName);
        }


        public void CopyFile(String source, String target, Boolean force) {
            if (force)
            {
                Delete(target);
            }
            CopyFile(new FileInfo(source), new FileInfo(target));
        }

        public void CopyFile(FileInfo source, FileInfo target)
        {
            source.CopyTo(target.ToString());
        }

        public void DeepCopy(String source, String target, Boolean force)
        {
            if (force)
            {
                Delete(target);
            }
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
                file.CopyTo(Path.Combine(target.FullName, file.Name), true);
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

        public void gitDispose(String path)
        {
            Repository repo = new Repository(path);
            repo.Dispose();
        }

        public void Delete(String path)
        {
            int tries = 0;
            while (true) {
                try
                {
                    FileAttributes attr = File.GetAttributes(path);

                    //detect whether its a directory or file
                    if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        var files = Directory.GetFiles(path);
                        var directories = Directory.GetDirectories(path);
                        foreach (var file in files)
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }

                        foreach (var dir in directories)
                        {
                            Delete(dir);
                        }
                        File.SetAttributes(path, FileAttributes.Normal);
                        Directory.Delete(path, false);
                    }
                    else
                    {
                        FileInfo file = new FileInfo(path);
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                    }
                    break;
                }
                catch(Exception e)
                {
                    if (tries > 3)
                    {
                        throw e;
                    }
                    System.Threading.Thread.Sleep(1 * 60 * 1000);  //60 seconds
                    tries = tries + 1;
                }
            }
        }
        
        public void Clone(String githubURL, String dest, String branch)
        {
            int tries = 0;
            while (tries <= 3)
            {
                try
                {
                    //_log.Info("cloning " + githubURL + " to " + dest);
                    Repository.Clone(githubURL, dest, new CloneOptions { BranchName = branch });
                    break;
                }
                catch (LibGit2Sharp.NameConflictException ex) //folder already exisits
                {
                    break;
                }
                catch (Exception ex)
                {
                    tries = tries + 1;
                    System.Threading.Thread.Sleep(1 * 60 * 1000); // sleep 1 min
                    if (tries > 3)
                    {
                        Delete(dest);
                        //_log.Info("delete repo" + githubURL);
                        throw ex;
                    }
                }
            }
        }

        // copy template folder to dest folder
        // apply changes to dockerFile
        public void FillTemplate(String dockerFile, List<String> newLines, List<int> lineNumbers)
        {
            // edit dockerfile
            //_log.Info("editing dockerfile");
            for (int i = 0; i < newLines.Count; i++) {
                LineChanger(newLines[i], dockerFile, lineNumbers[i]);
            }
        }

        public void Stage(String localRepo, String path) {
            // git add
            //_log.Info("git add");
            Commands.Stage(new Repository(localRepo), path);
        }

        public void CommitAndPush(String gitPath, String branch, String message)
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
                    repo.Network.Push(repo.Branches[branch], options);
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
