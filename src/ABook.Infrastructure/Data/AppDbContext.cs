using ABook.Core.Models;
using ABook.Infrastructure.VectorStore;
using Microsoft.EntityFrameworkCore;

namespace ABook.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<ChapterVersion> ChapterVersions => Set<ChapterVersion>();
    public DbSet<AgentMessage> AgentMessages => Set<AgentMessage>();
    public DbSet<LlmConfiguration> LlmConfigurations => Set<LlmConfiguration>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<TokenUsageRecord> TokenUsageRecords => Set<TokenUsageRecord>();
    public DbSet<StoryBible> StoryBibles => Set<StoryBible>();
    public DbSet<CharacterCard> CharacterCards => Set<CharacterCard>();
    public DbSet<PlotThread> PlotThreads => Set<PlotThread>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<ChapterEmbedding> ChapterEmbeddings => Set<ChapterEmbedding>();
    public DbSet<LlmPreset> LlmPresets => Set<LlmPreset>();
    public DbSet<StoryBibleSnapshot> StoryBibleSnapshots => Set<StoryBibleSnapshot>();
    public DbSet<CharactersSnapshot> CharactersSnapshots => Set<CharactersSnapshot>();
    public DbSet<PlotThreadsSnapshot> PlotThreadsSnapshots => Set<PlotThreadsSnapshot>();
    public DbSet<BookSnapshot> BookSnapshots => Set<BookSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<AppUser>(u =>
        {
            u.HasKey(x => x.Id);
            u.HasIndex(x => x.Username).IsUnique();
            u.Property(x => x.Username).IsRequired().HasMaxLength(100);
            u.Property(x => x.PasswordHash).IsRequired();
        });

        modelBuilder.Entity<Book>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).IsRequired().HasMaxLength(500);
            b.Property(x => x.Premise).IsRequired();
            b.Property(x => x.Genre).HasMaxLength(100);
            b.Property(x => x.Status).HasConversion<string>();
            b.Property(x => x.Language).HasMaxLength(100).HasDefaultValue("English");
            b.HasOne(x => x.User)
             .WithMany(x => x.Books)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);
        });

        modelBuilder.Entity<Chapter>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.Title).HasMaxLength(500);
            c.Property(x => x.Status).HasConversion<string>();
            c.Property(x => x.PovCharacter).HasDefaultValue(string.Empty);
            c.Property(x => x.CharactersInvolvedJson).HasDefaultValue("[]");
            c.Property(x => x.PlotThreadsJson).HasDefaultValue("[]");
            c.Property(x => x.ForeshadowingNotes).HasDefaultValue(string.Empty);
            c.Property(x => x.PayoffNotes).HasDefaultValue(string.Empty);
            c.Property(x => x.IsArchived).HasDefaultValue(false);
            c.HasOne(x => x.Book)
             .WithMany(x => x.Chapters)
             .HasForeignKey(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentMessage>(m =>
        {
            m.HasKey(x => x.Id);
            m.Property(x => x.AgentRole).HasConversion<string>();
            m.Property(x => x.MessageType).HasConversion<string>();
            m.HasOne(x => x.Book)
             .WithMany(x => x.AgentMessages)
             .HasForeignKey(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade);
            m.HasOne(x => x.Chapter)
             .WithMany(x => x.AgentMessages)
             .HasForeignKey(x => x.ChapterId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LlmConfiguration>(l =>
        {
            l.HasKey(x => x.Id);
            l.Property(x => x.Provider).HasConversion<string>();
            l.Property(x => x.ModelName).IsRequired().HasMaxLength(200);
            l.Property(x => x.Endpoint).HasMaxLength(500);
            l.HasOne(x => x.Book)
             .WithMany(x => x.LlmConfigurations)
             .HasForeignKey(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade)
             .IsRequired(false);
            l.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);

            l.HasData(new LlmConfiguration
            {
                Id = 1,
                BookId = null,
                Provider = LlmProvider.Ollama,
                ModelName = "llama3",
                Endpoint = "http://host.docker.internal:11434",
                EmbeddingModelName = "nomic-embed-text"
            });
        });

        modelBuilder.Entity<TokenUsageRecord>(t =>
        {
            t.HasKey(x => x.Id);
            t.Property(x => x.AgentRole).HasConversion<string>();
            t.HasOne(x => x.Book)
             .WithMany()
             .HasForeignKey(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade);
            t.HasOne(x => x.Chapter)
             .WithMany()
             .HasForeignKey(x => x.ChapterId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);
        });

        modelBuilder.Entity<StoryBible>(s =>
        {
            s.HasKey(x => x.Id);
            s.HasOne(x => x.Book)
             .WithOne(x => x.StoryBible)
             .HasForeignKey<StoryBible>(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CharacterCard>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.Name).IsRequired().HasMaxLength(200);
            c.Property(x => x.Role).HasConversion<string>();
            c.HasOne(x => x.Book)
             .WithMany(x => x.CharacterCards)
             .HasForeignKey(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlotThread>(p =>
        {
            p.HasKey(x => x.Id);
            p.Property(x => x.Name).IsRequired().HasMaxLength(300);
            p.Property(x => x.Type).HasConversion<string>();
            p.Property(x => x.Status).HasConversion<string>();
            p.HasOne(x => x.Book)
             .WithMany(x => x.PlotThreads)
             .HasForeignKey(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LlmPreset>(p =>
        {
            p.HasKey(x => x.Id);
            p.Property(x => x.Name).IsRequired().HasMaxLength(200);
            p.Property(x => x.Provider).HasConversion<string>();
            p.Property(x => x.ModelName).IsRequired().HasMaxLength(200);
            p.Property(x => x.Endpoint).HasMaxLength(500);
            p.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);
        });

        modelBuilder.Entity<AgentRun>(r =>
        {
            r.HasKey(x => x.Id);
            r.Property(x => x.RunType).IsRequired().HasMaxLength(50);
            r.Property(x => x.Status).HasConversion<string>();
            r.Property(x => x.CurrentRole).HasConversion<string>();
            r.HasOne(x => x.Book)
             .WithMany()
             .HasForeignKey(x => x.BookId)
             .OnDelete(DeleteBehavior.Cascade);
            r.HasOne(x => x.PendingMessage)
             .WithMany()
             .HasForeignKey(x => x.PendingMessageId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);
            r.HasIndex(x => new { x.BookId, x.Status });
        });

        modelBuilder.Entity<ChapterVersion>(v =>
        {
            v.HasKey(x => x.Id);
            v.Property(x => x.Title).HasMaxLength(500);
            v.Property(x => x.Status).HasConversion<string>();
            v.Property(x => x.CreatedBy).HasMaxLength(100);
            v.Property(x => x.PovCharacter).HasDefaultValue(string.Empty);
            v.Property(x => x.CharactersInvolvedJson).HasDefaultValue("[]");
            v.Property(x => x.PlotThreadsJson).HasDefaultValue("[]");
            v.Property(x => x.ForeshadowingNotes).HasDefaultValue(string.Empty);
            v.Property(x => x.PayoffNotes).HasDefaultValue(string.Empty);
            v.Property(x => x.IsActive).HasDefaultValue(false);
            v.Property(x => x.HasEmbeddings).HasDefaultValue(false);
            v.HasOne(x => x.Chapter)
             .WithMany(x => x.Versions)
             .HasForeignKey(x => x.ChapterId)
             .OnDelete(DeleteBehavior.Cascade);
            v.HasIndex(x => new { x.ChapterId, x.VersionNumber }).IsUnique();
            v.HasIndex(x => new { x.ChapterId, x.IsActive });
        });

        // Update ChapterEmbedding to include optional FK to ChapterVersion
        modelBuilder.Entity<ChapterEmbedding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Text).IsRequired();
            e.Property(x => x.Embedding).HasColumnType("vector");
            e.HasIndex(x => new { x.BookId, x.ChapterId, x.ChunkIndex }).IsUnique()
             .HasFilter("\"ChapterVersionId\" IS NULL");
            e.HasIndex(x => new { x.BookId, x.ChapterVersionId, x.ChunkIndex }).IsUnique()
             .HasFilter("\"ChapterVersionId\" IS NOT NULL");
            e.HasIndex(x => x.BookId);
        });

        modelBuilder.Entity<StoryBibleSnapshot>(s =>
        {
            s.HasKey(x => x.Id);
            s.Property(x => x.Reason).HasMaxLength(500);
            s.HasIndex(x => x.BookId);
        });

        modelBuilder.Entity<CharactersSnapshot>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.Reason).HasMaxLength(500);
            c.HasIndex(x => x.BookId);
        });

        modelBuilder.Entity<PlotThreadsSnapshot>(p =>
        {
            p.HasKey(x => x.Id);
            p.Property(x => x.Reason).HasMaxLength(500);
            p.HasIndex(x => x.BookId);
        });

        modelBuilder.Entity<BookSnapshot>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(500);
            b.Property(x => x.Genre).HasMaxLength(100);
            b.Property(x => x.Language).HasMaxLength(100);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.HasIndex(x => x.BookId);
        });
    }
}
