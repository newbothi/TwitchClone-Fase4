using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs
{
    public class UserProfileDto
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayUsername { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Points { get; set; }
        public bool IsLive { get; set; }
        public int FollowerCount { get; set; }
        public int FollowingCount { get; set; }
        public bool IsFollowedByCurrentUser { get; set; }
        public ChannelDto? Channel { get; set; }

        public string? AvatarUrl { get; set; }
        public string? BannerUrl { get; set; }
        public string? Bio { get; set; }
        public List<SocialLinkDto> SocialLinks { get; set; } = new();
    }

    public class SocialLinkDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }
}
