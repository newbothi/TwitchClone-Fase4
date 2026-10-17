using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data
{
    public class AppDbContext : IdentityDbContext<AppUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Channel> Channels { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<CategoryTag> CategoryTags { get; set; }
        public DbSet<Follow> Follows { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<Donation> Donations { get; set; }
        public DbSet<Clip> Clips { get; set; }
        public DbSet<Emote> Emotes { get; set; }
        public DbSet<BannedUser> BannedUsers { get; set; }
        public DbSet<SocialLink> SocialLinks { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ── Renombrar tablas de Identity ──────────────────────────
            builder.Entity<AppUser>().ToTable("Users");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityRole>().ToTable("Roles");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<string>>().ToTable("UserRoles");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<string>>().ToTable("UserClaims");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>().ToTable("UserLogins");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>>().ToTable("RoleClaims");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>().ToTable("UserTokens");

            // ── ApplicationUser ───────────────────────────────────────
            builder.Entity<AppUser>(e =>
            {
                e.Property(u => u.DisplayUsername)
                    .HasMaxLength(50)
                    .IsRequired();

                // Follows: un user puede seguir a muchos (Following)
                e.HasMany(u => u.Following)
                    .WithOne(f => f.Follower)
                    .HasForeignKey(f => f.FollowerId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Follows: un user puede tener muchos seguidores (Followers)
                e.HasMany(u => u.Followers)
                    .WithOne(f => f.Followed)
                    .HasForeignKey(f => f.FollowedId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Mensajes enviados
                e.HasMany(u => u.Messages)
                    .WithOne(m => m.User)
                    .HasForeignKey(m => m.UserId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Donaciones enviadas
                e.HasMany(u => u.DonationsSent)
                    .WithOne(d => d.FromUser)
                    .HasForeignKey(d => d.FromUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Enlaces sociales del perfil (hasta 5, ver UserController.AddSocialLink)
                e.HasMany(u => u.SocialLinks)
                    .WithOne(s => s.User)
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Channel ───────────────────────────────────────────────
            builder.Entity<Channel>(e =>
            {
                e.HasKey(c => c.Id);

                e.Property(c => c.Name)
                    .HasMaxLength(100)
                    .IsRequired();

                e.Property(c => c.Description)
                    .HasMaxLength(500);

                e.Property(c => c.StreamKey)
                    .HasMaxLength(200);

                // Relación 1:1 con ApplicationUser
                e.HasOne(c => c.User)
                    .WithOne(u => u.Channel)
                    .HasForeignKey<Channel>(c => c.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Relación con Category (opcional)
                e.HasOne(c => c.Category)
                    .WithMany(cat => cat.Channels)
                    .HasForeignKey(c => c.CategoryId)
                    .OnDelete(DeleteBehavior.SetNull);

                // Emotes del canal
                e.HasMany(c => c.Emotes)
                    .WithOne(em => em.Channel)
                    .HasForeignKey(em => em.ChannelId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Clips del canal
                e.HasMany(c => c.Clips)
                    .WithOne(cl => cl.Channel)
                    .HasForeignKey(cl => cl.ChannelId)
                    .OnDelete(DeleteBehavior.Cascade);

                // BannedUsers del canal
                e.HasMany(c => c.BannedUsers)
                    .WithOne(b => b.Channel)
                    .HasForeignKey(b => b.ChannelId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Category ──────────────────────────────────────────────
            builder.Entity<Category>(e =>
            {
                e.HasKey(c => c.Id);

                e.Property(c => c.Name)
                    .HasMaxLength(100)
                    .IsRequired();

                e.Property(c => c.Description)
                    .HasMaxLength(500);

                e.Property(c => c.ImageUrl)
                    .HasMaxLength(200);

                // Auto-referencia: categoría padre / sub-categorías (ej. Videojuegos -> Street Fighter 6)
                e.HasOne(c => c.ParentCategory)
                    .WithMany(c => c.SubCategories)
                    .HasForeignKey(c => c.ParentCategoryId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // ── Tag ───────────────────────────────────────────────────
            builder.Entity<Tag>(e =>
            {
                e.HasKey(t => t.Id);

                e.Property(t => t.Name)
                    .HasMaxLength(100)
                    .IsRequired();

                e.Property(t => t.Description)
                    .HasMaxLength(500);

                // Nombre de tag único (ej. no puede haber dos "IRL")
                e.HasIndex(t => t.Name)
                    .IsUnique();
            });

            // ── CategoryTag (unión N:M entre Category y Tag) ─────────────
            builder.Entity<CategoryTag>(e =>
            {
                e.HasKey(ct => ct.Id);

                e.HasOne(ct => ct.Category)
                    .WithMany(c => c.CategoryTags)
                    .HasForeignKey(ct => ct.CategoryId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(ct => ct.Tag)
                    .WithMany(t => t.CategoryTags)
                    .HasForeignKey(ct => ct.TagId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Evitar vincular la misma categoría al mismo tag dos veces
                e.HasIndex(ct => new { ct.CategoryId, ct.TagId })
                    .IsUnique();
            });

            // ── Follow ────────────────────────────────────────────────
            builder.Entity<Follow>(e =>
            {
                e.HasKey(f => f.Id);

                // Evitar follows duplicados
                e.HasIndex(f => new { f.FollowerId, f.FollowedId })
                    .IsUnique();
            });

            // ── Message ───────────────────────────────────────────────
            builder.Entity<Message>(e =>
            {
                e.HasKey(m => m.Id);

                e.Property(m => m.Content)
                    .HasMaxLength(500)
                    .IsRequired();

                e.HasOne(m => m.Channel)
                    .WithMany()
                    .HasForeignKey(m => m.ChannelId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Donation ──────────────────────────────────────────────
            builder.Entity<Donation>(e =>
            {
                e.HasKey(d => d.Id);

                e.Property(d => d.Amount)
                    .HasColumnType("decimal(18,2)")
                    .IsRequired();

                e.Property(d => d.Message)
                    .HasMaxLength(500);

                e.HasOne(d => d.Channel)
                    .WithMany()
                    .HasForeignKey(d => d.ChannelId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Clip ──────────────────────────────────────────────────
            builder.Entity<Clip>(e =>
            {
                e.HasKey(c => c.Id);

                e.Property(c => c.Title)
                    .HasMaxLength(200)
                    .IsRequired();

                e.Property(c => c.VideoUrl)
                    .HasMaxLength(500)
                    .IsRequired();

                // Relación con el user que creó el clip
                e.HasOne(c => c.CreatedBy)
                    .WithMany()
                    .HasForeignKey(c => c.CreatedByUserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // ── Emote ─────────────────────────────────────────────────
            builder.Entity<Emote>(e =>
            {
                e.HasKey(em => em.Id);

                e.Property(em => em.Code)
                    .HasMaxLength(50)
                    .IsRequired();

                e.Property(em => em.ImageUrl)
                    .HasMaxLength(500)
                    .IsRequired();

                // Código único por canal
                e.HasIndex(em => new { em.ChannelId, em.Code })
                    .IsUnique();
            });

            // ── BannedUser ────────────────────────────────────────────
            builder.Entity<BannedUser>(e =>
            {
                e.HasKey(b => b.Id);

                e.Property(b => b.Reason)
                    .HasMaxLength(500);

                // Quien fue baneado
                e.HasOne(b => b.User)
                    .WithMany()
                    .HasForeignKey(b => b.UserId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Quien hizo el ban
                e.HasOne(b => b.BannedBy)
                    .WithMany()
                    .HasForeignKey(b => b.BannedById)
                    .OnDelete(DeleteBehavior.Restrict);

                // Un usuario baneado una sola vez por canal
                e.HasIndex(b => new { b.UserId, b.ChannelId })
                    .IsUnique();
            });

            // ── SocialLink ────────────────────────────────────────────
            // Nota: el máximo de 5 enlaces por usuario (ver la pantalla
            // "Enlaces sociales" del perfil) se valida en el controlador,
            // no aquí — SQL Server no expresa "máximo N filas por FK"
            // como constraint declarativa.
            builder.Entity<SocialLink>(e =>
            {
                e.HasKey(s => s.Id);

                e.Property(s => s.Title)
                    .HasMaxLength(50)
                    .IsRequired();

                e.Property(s => s.Url)
                    .HasMaxLength(300)
                    .IsRequired();
            });
        }
    }
}

