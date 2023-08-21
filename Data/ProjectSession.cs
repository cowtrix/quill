﻿using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using System.Text.Json;

namespace Squill.Data;

public class ProjectSession
{
    public ProjectSession(Project project)
    {
        Project = project;
        m_factory = new ElementFactory(this);
        m_elementCache = new Dictionary<Guid, IElement>();
    }

    private static string Username => "cowtrix";
    private static string Email = "seandgfinnegan@gmail.com";

    public const string MASTER_NAME = "squill_master";

    public Project Project { get; set; }
    public IEnumerable<ElementMetaData> ElementMeta => m_elementMetadata.Values;
    public bool IsSynchronized { get; set; }
    public event EventHandler OnSynchronized;

    private Repository m_repository;
    private Task? m_workerTask;
    private ElementFactory m_factory;
    private Dictionary<Guid, ElementMetaData> m_elementMetadata;
    private Dictionary<Guid, IElement> m_elementCache;

    public void TryStartSynchronize()
    {
        if (m_workerTask != null)
        {
            return;
        }
        m_workerTask = new Task(async () => await SynchronizeAsync());
        m_workerTask.Start();
    }

    private async Task SynchronizeAsync()
    {
        if (!Project.IsConfigured)
        {
            throw new Exception("Project not configured");
        }
        if (!Directory.Exists(Project.DataDir))
        {
            Repository.Clone(Project.RepositoryURL, Project.DataDir, new CloneOptions { CredentialsProvider = GetCredentials });
        }
        m_repository = new Repository(Project.DataDir);
        if (!m_repository.Branches.Any(b => b.FriendlyName == MASTER_NAME))
        {
            m_repository.CreateBranch(MASTER_NAME);
        }
        Commands.Checkout(m_repository, m_repository.Branches.Single(b => b.FriendlyName == MASTER_NAME));

        m_elementMetadata = Directory.GetFiles(Project.DataDir, "*.meta", SearchOption.AllDirectories)
            .Select(p => m_factory.GetMetaData(p))
            .ToDictionary(e => Guid.Parse(e.Guid), e => e);

        IsSynchronized = true;
        OnSynchronized?.Invoke(this, null);
        m_workerTask = null;
    }

    public async Task Save()
    {
        if (!IsSynchronized)
        {
            throw new Exception();
        }
        Commands.Stage(m_repository, Directory.GetFiles(Project.DataDir));
    }

    private Credentials GetCredentials(string url, string usernameFromUrl, SupportedCredentialTypes types)
    {
        if (url != Project.RepositoryURL)
        {
            throw new Exception();
        }
        return new UsernamePasswordCredentials { Username = Username, Password = Project.RepositoryToken };
    }

    public async Task<IElement> CreateNewElement(Type t)
    {
        if (!Project.IsConfigured || !IsSynchronized)
        {
            throw new Exception();
        }
        var newElement = m_factory.CreateNewElement(t);
        m_elementMetadata[newElement.Item2.Guid] = newElement.Item1;
        return newElement.Item2;
    }

    public T GetElement<T>(ElementMetaData metaData) where T : class
    {
        if (!m_elementCache.TryGetValue(Guid.Parse(metaData.Guid), out var ele))
        {
            ele = m_factory.GetElementAtPath(metaData.Path);
            m_elementCache[ele.Guid] = ele;
        }
        return (T)ele;
    }

    public ElementMetaData? GetMetaData(Guid guid)
    {
        if (!m_elementMetadata.TryGetValue(guid, out var ele))
        {
            return null;
        }
        return ele;
    }

    public async Task UpdateElement(IElement element)
    {
        var meta = GetMetaData(element.Guid);
        meta.LastModified = DateTimeOffset.UtcNow.Ticks;
        meta.Attributes = element.GetAttributes().ToDictionary(s => s.Item1, s => s.Item2);
        await File.WriteAllTextAsync(meta.Path, JsonSerializer.Serialize(element, element.GetType()));
        await File.WriteAllTextAsync(meta.Path + ".meta", JsonSerializer.Serialize(meta));
    }

    public async Task RenameElement(ElementMetaData meta, string newName)
    {
        if (meta.Name == newName)
        {
            return;
        }
        var newPath = Path.Combine(Path.GetDirectoryName(meta.Path), newName + Path.GetExtension(meta.Path));
        File.Copy(meta.Path, newPath);
        File.Delete(meta.Path);
        File.Delete(meta.Path + ".meta");
        meta.Path = newPath;
        meta.Name = newName;
        await File.WriteAllTextAsync(meta.Path + ".meta", JsonSerializer.Serialize(meta));
    }

    public int GetTypeCount(Type t) => m_elementMetadata.Count(m => m.Value.Type == t.FullName);

    public TreeChanges? GetGitDiff()
    {
        if (m_repository == null)
        {
            return null;
        }
        Commands.Stage(m_repository, "*");
        return m_repository.Diff.Compare<TreeChanges>(m_repository.Head.Tip.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory);
    }

    public void Commit(string message, bool userTriggerd)
    {
        var sig = new Signature(Username, Email, DateTimeOffset.Now);
        if (GetGitDiff().Any())
        {
            m_repository.Commit($"[SYNC] {message}{(userTriggerd ? " [Explicit]" : "")}", sig, sig);
        }
        var remote = m_repository.Network.Remotes["origin"];
        var options = new PushOptions();
        options.CredentialsProvider = GetCredentials;
        var pushRefSpec = $"refs/heads/{MASTER_NAME}";
        m_repository.Network.Push(remote, pushRefSpec, options);
    }
}
