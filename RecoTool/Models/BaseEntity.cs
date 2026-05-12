using System;
using RecoTool.Infrastructure.Time;

namespace RecoTool.Models
{
    /// <summary>
    /// Classe de base pour toutes les entités avec gestion de la synchronisation.
    /// <para>
    /// Les estampes temporelles passent par <see cref="Clock"/> (par défaut
    /// <see cref="SystemClock.Instance"/>). Les tests qui veulent des dates
    /// déterministes peuvent assigner un fake clock global avant d'instancier
    /// les entités.
    /// </para>
    /// </summary>
    public abstract class BaseEntity
    {
        /// <summary>Clock global pour les estampes temporelles. Swappable en tests.</summary>
        public static IClock Clock { get; set; } = SystemClock.Instance;

        public DateTime? CreationDate { get; set; }
        public DateTime? DeleteDate { get; set; }
        public string ModifiedBy { get; set; }
        public DateTime? LastModified { get; set; }
        public int Version { get; set; }

        protected BaseEntity()
        {
            CreationDate = Clock.Now;
            Version = 1;
        }

        /// <summary>
        /// Indique si l'entité est supprimée (logiquement)
        /// </summary>
        public bool IsDeleted => DeleteDate.HasValue;

        /// <summary>
        /// Marque l'entité comme supprimée
        /// </summary>
        public virtual void MarkAsDeleted()
        {
            DeleteDate = Clock.Now;
        }

        /// <summary>
        /// Met à jour les informations de modification
        /// </summary>
        /// <param name="modifiedBy">Utilisateur qui modifie</param>
        public virtual void UpdateModification(string modifiedBy)
        {
            ModifiedBy = modifiedBy;
            LastModified = Clock.Now;
            Version++;
        }
    }
}
