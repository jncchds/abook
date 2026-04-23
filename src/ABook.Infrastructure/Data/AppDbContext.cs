using ABook.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ABook.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<AgentMessage> AgentMessages => Set<AgentMessage>();
    public DbSet<LlmConfiguration> LlmConfigurations => Set<LlmConfiguration>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<TokenUsageRecord> TokenUsageRecords => Set<TokenUsageRecord>();
    public DbSet<StoryBible> StoryBibles => Set<StoryBible>();
    public DbSet<CharacterCard> CharacterCards => Set<CharacterCard>();
    public DbSet<PlotThread> PlotThreads => Set<PlotThread>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
    }
}
