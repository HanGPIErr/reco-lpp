using RecoTool.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Web;

namespace RecoTool.API
{
    public class FreeSearchDetails
    {
        public class MessageStatus
        {
            [JsonPropertyName("code")]
            public string Code { get; set; }

            [JsonPropertyName("codeComplementaire")]
            public string CodeComplementaire { get; set; }

            [JsonPropertyName("authorisedDisplayDefect")]
            public bool AuthorisedDisplayDefect { get; set; }
        }

        public class Emetteur
        {
            [JsonPropertyName("identifiant")]
            public string Identifiant { get; set; }

            [JsonPropertyName("identifiantDN")]
            public string IdentifiantDN { get; set; }

            [JsonPropertyName("libelle")]
            public string Libelle { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("branchCode")]
            public string BranchCode { get; set; }
        }

        public class Recepteur
        {
            [JsonPropertyName("identifiant")]
            public string Identifiant { get; set; }

            [JsonPropertyName("identifiantDN")]
            public string IdentifiantDN { get; set; }

            [JsonPropertyName("libelle")]
            public string Libelle { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("branchCode")]
            public string BranchCode { get; set; }
        }

        public class TexteMessage
        {
            [JsonPropertyName("texte")]
            public string Texte { get; set; }

            [JsonPropertyName("formatMessage")]
            public string FormatMessage { get; set; }

            [JsonPropertyName("texteFormatte")]
            public string TexteFormatte { get; set; }

            [JsonPropertyName("texteExportMX")]
            public string TexteExportMX { get; set; }

            [JsonPropertyName("texteExportMT")]
            public string TexteExportMT { get; set; }

            [JsonPropertyName("typeMessage")]
            public string TypeMessage { get; set; }
        }



        public class AuditEvent
        {
            [JsonPropertyName("date")]
            public string Date { get; set; }

            [JsonPropertyName("heure")]
            public string Heure { get; set; }

            [JsonPropertyName("evenement")]
            public string Evenement { get; set; }

            [JsonPropertyName("evenementDescription")]
            public string EvenementDescription { get; set; }

            [JsonPropertyName("evenementStatus")]
            public string EvenementStatus { get; set; }

            [JsonPropertyName("evenementDescriptionMessageKey")]
            public string EvenementDescriptionMessageKey { get; set; }
        }

        public class SsChamp
        {
            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("value")]
            public string Value { get; set; }
        }

        public class Champ
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("tagName")]
            public string TagName { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("value")]
            public string Value { get; set; }

            [JsonPropertyName("listeSsChamps")]
            public List<SsChamp> ListeSsChamps { get; set; }

            [JsonPropertyName("erreur")]
            public List<string> Erreur { get; set; }

            [JsonPropertyName("nameBDD")]
            public string NameBDD { get; set; }
        }

        public class FormatEtendu
        {
            [JsonPropertyName("listeChamps")]
            public List<Champ> ListeChamps { get; set; }

            [JsonPropertyName("erreur")]
            public string Erreur { get; set; }

            [JsonPropertyName("texteFormatEtendu")]
            public string TexteFormatEtendu { get; set; }

            [JsonPropertyName("locale")]
            public string Locale { get; set; }

            [JsonPropertyName("partieMT")]
            public string PartieMT { get; set; }
        }


        public class Message
        {
            [JsonPropertyName("idMessage")]
            public string IdMessage { get; set; }

            [JsonPropertyName("sens")]
            public string Sens { get; set; }

            [JsonPropertyName("date")]
            public string Date { get; set; }

            [JsonPropertyName("heure")]
            public string Heure { get; set; }

            [JsonPropertyName("referenceMurImr")]
            public string ReferenceMurImr { get; set; }

            [JsonPropertyName("referenceTRN")]
            public string ReferenceTRN { get; set; }

            [JsonPropertyName("referenceLiee")]
            public string ReferenceLiee { get; set; }

            [JsonPropertyName("codeApplication")]
            public string CodeApplication { get; set; }

            [JsonPropertyName("codeService")]
            public string CodeService { get; set; }

            [JsonPropertyName("statusMsgSibes")]
            public MessageStatus StatusMsgSibes { get; set; }

            [JsonPropertyName("statutSIBES")]
            public string StatutSIBES { get; set; }

            [JsonPropertyName("statutSIBESEdition")]
            public string StatutSIBESEdition { get; set; }

            [JsonPropertyName("statutSIBESText")]
            public string StatutSIBESText { get; set; }

            [JsonPropertyName("typeReseau")]
            public string TypeReseau { get; set; }

            [JsonPropertyName("typeSousReseau")]
            public string TypeSousReseau { get; set; }

            [JsonPropertyName("connector")]
            public string Connector { get; set; }

            [JsonPropertyName("typeReseauText")]
            public string TypeReseauText { get; set; }

            [JsonPropertyName("typeReseauEdition")]
            public string TypeReseauEdition { get; set; }

            [JsonPropertyName("formatMessage")]
            public string FormatMessage { get; set; }

            [JsonPropertyName("typeMessage")]
            public string TypeMessage { get; set; }

            [JsonPropertyName("typeAck")]
            public string TypeAck { get; set; }

            [JsonPropertyName("swiftnet")]
            public string Swiftnet { get; set; }

            [JsonPropertyName("emetteur")]
            public Emetteur Emetteur { get; set; }

            [JsonPropertyName("recepteur")]
            public Recepteur Recepteur { get; set; }

            [JsonPropertyName("adresseGroup")]
            public string AdresseGroup { get; set; }

            [JsonPropertyName("statutSHINE")]
            public string StatutSHINE { get; set; }

            [JsonPropertyName("statutOLAF")]
            public string StatutOLAF { get; set; }

            [JsonPropertyName("statutSHINEEdition")]
            public string StatutSHINEEdition { get; set; }

            [JsonPropertyName("statutOLAFEdition")]
            public string StatutOLAFEdition { get; set; }

            [JsonPropertyName("codeDevise")]
            public string CodeDevise { get; set; }

            [JsonPropertyName("montant")]
            public string Montant { get; set; }

            [JsonPropertyName("montantNumerique")]
            public double MontantNumerique { get; set; }

            [JsonPropertyName("dateValeur")]
            public string DateValeur { get; set; }

            [JsonPropertyName("champSw103")]
            public string ChampSw103 { get; set; }

            [JsonPropertyName("referenceMessage")]
            public string ReferenceMessage { get; set; }

            [JsonPropertyName("villeCorrespondant")]
            public string VilleCorrespondant { get; set; }

            [JsonPropertyName("codeBranche")]
            public string CodeBranche { get; set; }

            [JsonPropertyName("refFrontaleMIR")]
            public string RefFrontaleMIR { get; set; }

            [JsonPropertyName("refFrontaleMOR")]
            public string RefFrontaleMOR { get; set; }

            [JsonPropertyName("listeAudit")]
            public List<AuditEvent> ListeAudit { get; set; }

            [JsonPropertyName("listeDefect")]
            public List<string> ListeDefect { get; set; }

            [JsonPropertyName("repetition")]
            public string Repetition { get; set; }

            [JsonPropertyName("repetitionText")]
            public string RepetitionText { get; set; }

            [JsonPropertyName("repetitionEdition")]
            public string RepetitionEdition { get; set; }

            [JsonPropertyName("dateFormatEtendu")]
            public string DateFormatEtendu { get; set; }

            [JsonPropertyName("texteMessage")]
            public TexteMessage TexteMessage { get; set; }

            [JsonPropertyName("formatEtendu")]
            public FormatEtendu FormatEtendu { get; set; }

            [JsonPropertyName("beneficiary")]
            public string Beneficiary { get; set; }

            [JsonPropertyName("orderingCustomer")]
            public string OrderingCustomer { get; set; }

            [JsonPropertyName("senderToReceiver")]
            public string SenderToReceiver { get; set; }

            [JsonPropertyName("remittanceInformation")]
            public string RemittanceInformation { get; set; }

            [JsonPropertyName("tag111")]
            public string Tag111 { get; set; }

            [JsonPropertyName("tag121")]
            public string Tag121 { get; set; }

            [JsonPropertyName("creditor")]
            public string Creditor { get; set; }

            [JsonPropertyName("debitor")]
            public string Debitor { get; set; }

            [JsonPropertyName("codeTranslation")]
            public string CodeTranslation { get; set; }

            [JsonPropertyName("txtMtTrd")]
            public string TxtMtTrd { get; set; }

            [JsonPropertyName("formatEtTypeMessage")]
            public string FormatEtTypeMessage { get; set; }

            [JsonPropertyName("referenceTRNEdition")]
            public string ReferenceTRNEdition { get; set; }

            [JsonPropertyName("statutSHINEText")]
            public string StatutSHINEText { get; set; }

            [JsonPropertyName("statutOLAFText")]
            public string StatutOLAFText { get; set; }

            [JsonPropertyName("codeDeviseEdition")]
            public string CodeDeviseEdition { get; set; }

            [JsonPropertyName("montantEdition")]
            public string MontantEdition { get; set; }

            [JsonPropertyName("dateValeurEdition")]
            public string DateValeurEdition { get; set; }

            [JsonPropertyName("refFrontaleMIRText")]
            public string RefFrontaleMIRText { get; set; }

            [JsonPropertyName("refFrontaleMORText")]
            public string RefFrontaleMORText { get; set; }

            [JsonPropertyName("texteExportMTFe")]
            public string TexteExportMTFe { get; set; }

            [JsonPropertyName("multiFormats")]
            public string MultiFormats { get; set; }
        }

        public class Root
        {
            [JsonPropertyName("idMessage")]
            public string IdMessage { get; set; }

            [JsonPropertyName("sens")]
            public string Sens { get; set; }

            [JsonPropertyName("date")]
            public string Date { get; set; }

            [JsonPropertyName("heure")]
            public string Heure { get; set; }

            [JsonPropertyName("referenceMurImr")]
            public string ReferenceMurImr { get; set; }

            [JsonPropertyName("referenceTRN")]
            public string ReferenceTRN { get; set; }

            [JsonPropertyName("referenceLiee")]
            public string ReferenceLiee { get; set; }

            [JsonPropertyName("codeApplication")]
            public string CodeApplication { get; set; }

            [JsonPropertyName("codeService")]
            public string CodeService { get; set; }

            [JsonPropertyName("statusMsgSibes")]
            public MessageStatus StatusMsgSibes { get; set; }

            [JsonPropertyName("statutSIBES")]
            public string StatutSIBES { get; set; }

            [JsonPropertyName("statutSIBESEdition")]
            public string StatutSIBESEdition { get; set; }

            [JsonPropertyName("statutSIBESText")]
            public string StatutSIBESText { get; set; }

            [JsonPropertyName("typeReseau")]
            public string TypeReseau { get; set; }

            [JsonPropertyName("typeSousReseau")]
            public string TypeSousReseau { get; set; }

            [JsonPropertyName("connector")]
            public string Connector { get; set; }

            [JsonPropertyName("typeReseauText")]
            public string TypeReseauText { get; set; }

            [JsonPropertyName("typeReseauEdition")]
            public string TypeReseauEdition { get; set; }

            [JsonPropertyName("formatMessage")]
            public string FormatMessage { get; set; }

            [JsonPropertyName("typeMessage")]
            public string TypeMessage { get; set; }

            [JsonPropertyName("typeAck")]
            public string TypeAck { get; set; }

            [JsonPropertyName("swiftnet")]
            public string Swiftnet { get; set; }

            [JsonPropertyName("emetteur")]
            public Emetteur Emetteur { get; set; }

            [JsonPropertyName("recepteur")]
            public Recepteur Recepteur { get; set; }

            [JsonPropertyName("adresseGroup")]
            public string AdresseGroup { get; set; }

            [JsonPropertyName("statutSHINE")]
            public string StatutSHINE { get; set; }

            [JsonPropertyName("statutOLAF")]
            public string StatutOLAF { get; set; }

            [JsonPropertyName("statutSHINEEdition")]
            public string StatutSHINEEdition { get; set; }

            [JsonPropertyName("statutOLAFEdition")]
            public string StatutOLAFEdition { get; set; }

            [JsonPropertyName("codeDevise")]
            public string CodeDevise { get; set; }

            [JsonPropertyName("montant")]
            public string Montant { get; set; }

            [JsonPropertyName("montantNumerique")]
            public double MontantNumerique { get; set; }

            [JsonPropertyName("dateValeur")]
            public string DateValeur { get; set; }

            [JsonPropertyName("champSw103")]
            public string ChampSw103 { get; set; }

            [JsonPropertyName("referenceMessage")]
            public string ReferenceMessage { get; set; }

            [JsonPropertyName("villeCorrespondant")]
            public string VilleCorrespondant { get; set; }

            [JsonPropertyName("codeBranche")]
            public string CodeBranche { get; set; }

            [JsonPropertyName("refFrontaleMIR")]
            public string RefFrontaleMIR { get; set; }

            [JsonPropertyName("refFrontaleMOR")]
            public string RefFrontaleMOR { get; set; }

            [JsonPropertyName("listeAudit")]
            public List<AuditEvent> ListeAudit { get; set; }

            [JsonPropertyName("listeDefect")]
            public List<string> ListeDefect { get; set; }

            [JsonPropertyName("repetition")]
            public string Repetition { get; set; }

            [JsonPropertyName("repetitionText")]
            public string RepetitionText { get; set; }

            [JsonPropertyName("repetitionEdition")]
            public string RepetitionEdition { get; set; }

            [JsonPropertyName("dateFormatEtendu")]
            public string DateFormatEtendu { get; set; }

            [JsonPropertyName("texteMessage")]
            public TexteMessage TexteMessage { get; set; }

            [JsonPropertyName("formatEtendu")]
            public FormatEtendu FormatEtendu { get; set; }

            [JsonPropertyName("beneficiary")]
            public string Beneficiary { get; set; }

            [JsonPropertyName("senderToReceiver")]
            public string SenderToReceiver { get; set; }

            [JsonPropertyName("remittanceInformation")]
            public string RemittanceInformation { get; set; }

            [JsonPropertyName("tag111")]
            public string Tag111 { get; set; }

            [JsonPropertyName("tag121")]
            public string Tag121 { get; set; }

            [JsonPropertyName("creditor")]
            public string Creditor { get; set; }

            [JsonPropertyName("debitor")]
            public string Debitor { get; set; }

            [JsonPropertyName("codeTranslation")]
            public string CodeTranslation { get; set; }

            [JsonPropertyName("txtMtTrd")]
            public string TxtMtTrd { get; set; }

            [JsonPropertyName("formatEtTypeMessage")]
            public string FormatEtTypeMessage { get; set; }

            [JsonPropertyName("referenceTRNEdition")]
            public string ReferenceTRNEdition { get; set; }

            [JsonPropertyName("statutSHINEText")]
            public string StatutSHINEText { get; set; }

            [JsonPropertyName("statutOLAFText")]
            public string StatutOLAFText { get; set; }

            [JsonPropertyName("codeDeviseEdition")]
            public string CodeDeviseEdition { get; set; }

            [JsonPropertyName("montantEdition")]
            public string MontantEdition { get; set; }

            [JsonPropertyName("dateValeurEdition")]
            public string DateValeurEdition { get; set; }

            [JsonPropertyName("refFrontaleMIRText")]
            public string RefFrontaleMIRText { get; set; }

            [JsonPropertyName("refFrontaleMORText")]
            public string RefFrontaleMORText { get; set; }

            [JsonPropertyName("texteExportMTFe")]
            public string TexteExportMTFe { get; set; }

            [JsonPropertyName("multiFormats")]
            public string MultiFormats { get; set; }
        }
    }


    public class FreeSearchItems
    {
        public class MessageStatus
        {
            [JsonPropertyName("code")]
            [DisplayName("Code")]
            public string Code { get; set; }

            [JsonPropertyName("codeComplementaire")]
            [DisplayName("Code Complementaire")]
            public string CodeComplementaire { get; set; }

            [JsonPropertyName("authorisedDisplayDefect")]
            [DisplayName("Authorised Display Defect")]
            public bool AuthorisedDisplayDefect { get; set; }
        }

        public class Emetteur
        {
            [JsonPropertyName("identifiant")]
            [DisplayName("Identifiant")]
            public string Identifiant { get; set; }

            [JsonPropertyName("identifiantDN")]
            [DisplayName("Identifiant DN")]
            public string IdentifiantDN { get; set; }

            [JsonPropertyName("libelle")]
            [DisplayName("Libelle")]
            public string Libelle { get; set; }

            [JsonPropertyName("type")]
            [DisplayName("Type")]
            public string Type { get; set; }

            [JsonPropertyName("branchCode")]
            [DisplayName("Branch Code")]
            public string BranchCode { get; set; }
        }

        public class Recepteur
        {
            [JsonPropertyName("identifiant")]
            [DisplayName("Identifiant")]
            public string Identifiant { get; set; }

            [JsonPropertyName("identifiantDN")]
            [DisplayName("Identifiant DN")]
            public string IdentifiantDN { get; set; }

            [JsonPropertyName("libelle")]
            [DisplayName("Libelle")]
            public string Libelle { get; set; }

            [JsonPropertyName("type")]
            [DisplayName("Type")]
            public string Type { get; set; }

            [JsonPropertyName("branchCode")]
            [DisplayName("Branch Code")]
            public string BranchCode { get; set; }
        }

        public class TexteMessage
        {
            [JsonPropertyName("texte")]
            [DisplayName("Texte")]
            public string Texte { get; set; }

            [JsonPropertyName("formatMessage")]
            [DisplayName("Format Message")]
            public string FormatMessage { get; set; }

            [JsonPropertyName("texteFormatte")]
            [DisplayName("Texte Formatte")]
            public string TexteFormatte { get; set; }

            [JsonPropertyName("texteExportMX")]
            [DisplayName("Texte Export MX")]
            public string TexteExportMX { get; set; }

            [JsonPropertyName("texteExportMT")]
            [DisplayName("Texte Export MT")]
            public string TexteExportMT { get; set; }

            [JsonPropertyName("typeMessage")]
            [DisplayName("Type Message")]
            public string TypeMessage { get; set; }
        }

        public class FormatEtendu
        {
            [JsonPropertyName("listeChamps")]
            [DisplayName("Liste Champs")]
            public List<string> ListeChamps { get; set; }

            [JsonPropertyName("erreur")]
            [DisplayName("Erreur")]
            public string Erreur { get; set; }

            [JsonPropertyName("texteFormatEtendu")]
            [DisplayName("Texte Format Etendu")]
            public string TexteFormatEtendu { get; set; }

            [JsonPropertyName("locale")]
            [DisplayName("Locale")]
            public string Locale { get; set; }

            [JsonPropertyName("partieMT")]
            [DisplayName("Partie MT")]
            public string PartieMT { get; set; }
        }

        public class Message
        {
            public FreeSearchDetails.Root SearchDetails { get; set; }

            [JsonPropertyName("idMessage")]
            [DisplayName("ID Message")]
            public string IdMessage { get; set; }

            [JsonPropertyName("sens")]
            [DisplayName("Sens")]
            public string Sens { get; set; }

            [JsonPropertyName("date")]
            [DisplayName("Date")]
            public string Date { get; set; }

            [JsonPropertyName("heure")]
            [DisplayName("Heure")]
            public string Heure { get; set; }

            [JsonPropertyName("referenceMurImr")]
            [DisplayName("Reference MUR/IMR")]
            public string ReferenceMurImr { get; set; }

            [JsonPropertyName("referenceTRN")]
            [DisplayName("Reference TRN")]
            public string ReferenceTRN { get; set; }

            [JsonPropertyName("referenceLiee")]
            [DisplayName("Reference Liee")]
            public string ReferenceLiee { get; set; }

            [JsonPropertyName("codeApplication")]
            [DisplayName("Code Application")]
            public string CodeApplication { get; set; }

            [JsonPropertyName("codeService")]
            [DisplayName("Code Service")]
            public string CodeService { get; set; }

            [JsonPropertyName("statusMsgSibes")]
            [DisplayName("Status Msg Sibes")]
            public MessageStatus StatusMsgSibes { get; set; }

            [JsonPropertyName("statutSIBES")]
            [DisplayName("Statut SIBES")]
            public string StatutSIBES { get; set; }

            [JsonPropertyName("statutSIBESEdition")]
            [DisplayName("Statut SIBES Edition")]
            public string StatutSIBESEdition { get; set; }

            [JsonPropertyName("statutSIBESText")]
            [DisplayName("Statut SIBES Text")]
            public string StatutSIBESText { get; set; }

            [JsonPropertyName("typeReseau")]
            [DisplayName("Type Reseau")]
            public string TypeReseau { get; set; }

            [JsonPropertyName("typeSousReseau")]
            [DisplayName("Type Sous Reseau")]
            public string TypeSousReseau { get; set; }

            [JsonPropertyName("connector")]
            [DisplayName("Connector")]
            public string Connector { get; set; }

            [JsonPropertyName("typeReseauText")]
            [DisplayName("Type Reseau Text")]
            public string TypeReseauText { get; set; }

            [JsonPropertyName("typeReseauEdition")]
            [DisplayName("Type Reseau Edition")]
            public string TypeReseauEdition { get; set; }

            [JsonPropertyName("formatMessage")]
            [DisplayName("Format Message")]
            public string FormatMessage { get; set; }

            [JsonPropertyName("typeMessage")]
            [DisplayName("Type Message")]
            public string TypeMessage { get; set; }

            [JsonPropertyName("typeAck")]
            [DisplayName("Type Ack")]
            public string TypeAck { get; set; }

            [JsonPropertyName("swiftnet")]
            [DisplayName("Swiftnet")]
            public string Swiftnet { get; set; }

            [JsonPropertyName("emetteur")]
            [DisplayName("Emetteur")]
            public Emetteur Emetteur { get; set; }

            [JsonPropertyName("recepteur")]
            [DisplayName("Recepteur")]
            public Recepteur Recepteur { get; set; }

            [JsonPropertyName("adresseGroup")]
            [DisplayName("Adresse Group")]
            public string AdresseGroup { get; set; }

            [JsonPropertyName("statutSHINE")]
            [DisplayName("Statut SHINE")]
            public string StatutSHINE { get; set; }

            [JsonPropertyName("statutOLAF")]
            [DisplayName("Statut OLAF")]
            public string StatutOLAF { get; set; }

            [JsonPropertyName("statutSHINEEdition")]
            [DisplayName("Statut SHINE Edition")]
            public string StatutSHINEEdition { get; set; }

            [JsonPropertyName("statutOLAFEdition")]
            [DisplayName("Statut OLAF Edition")]
            public string StatutOLAFEdition { get; set; }

            [JsonPropertyName("codeDevise")]
            [DisplayName("Code Devise")]
            public string CodeDevise { get; set; }

            [JsonPropertyName("montant")]
            [DisplayName("Montant")]
            public string Montant { get; set; }

            [JsonPropertyName("montantNumerique")]
            [DisplayName("Montant Numerique")]
            public double MontantNumerique { get; set; }

            [JsonPropertyName("dateValeur")]
            [DisplayName("Date Valeur")]
            public string DateValeur { get; set; }

            [JsonPropertyName("champSw103")]
            [DisplayName("Champ Sw103")]
            public string ChampSw103 { get; set; }

            [JsonPropertyName("referenceMessage")]
            [DisplayName("Reference Message")]
            public string ReferenceMessage { get; set; }

            [JsonPropertyName("villeCorrespondant")]
            [DisplayName("Ville Correspondant")]
            public string VilleCorrespondant { get; set; }

            [JsonPropertyName("codeBranche")]
            [DisplayName("Code Branche")]
            public string CodeBranche { get; set; }

            [JsonPropertyName("refFrontaleMIR")]
            [DisplayName("Ref Frontale MIR")]
            public string RefFrontaleMIR { get; set; }

            [JsonPropertyName("refFrontaleMOR")]
            [DisplayName("Ref Frontale MOR")]
            public string RefFrontaleMOR { get; set; }

            [JsonPropertyName("listeAudit")]
            [DisplayName("Liste Audit")]
            public List<string> ListeAudit { get; set; }

            [JsonPropertyName("listeDefect")]
            [DisplayName("Liste Defect")]
            public List<string> ListeDefect { get; set; }

            [JsonPropertyName("repetition")]
            [DisplayName("Repetition")]
            public string Repetition { get; set; }

            [JsonPropertyName("repetitionText")]
            [DisplayName("Repetition Text")]
            public string RepetitionText { get; set; }

            [JsonPropertyName("repetitionEdition")]
            [DisplayName("Repetition Edition")]
            public string RepetitionEdition { get; set; }

            [JsonPropertyName("dateFormatEtendu")]
            [DisplayName("Date Format Etendu")]
            public string DateFormatEtendu { get; set; }

            [JsonPropertyName("texteMessage")]
            [DisplayName("Texte Message")]
            public TexteMessage TexteMessage { get; set; }

            [JsonPropertyName("formatEtendu")]
            [DisplayName("Format Etendu")]
            public FormatEtendu FormatEtendu { get; set; }

            [JsonPropertyName("beneficiary")]
            [DisplayName("Beneficiary")]
            public string Beneficiary { get; set; }

            [JsonPropertyName("orderingCustomer")]
            [DisplayName("Ordering Customer")]
            public string OrderingCustomer { get; set; }

            [JsonPropertyName("senderToReceiver")]
            [DisplayName("Sender To Receiver")]
            public string SenderToReceiver { get; set; }

            [JsonPropertyName("remittanceInformation")]
            [DisplayName("Remittance Information")]
            public string RemittanceInformation { get; set; }

            [JsonPropertyName("tag111")]
            [DisplayName("Tag 111")]
            public string Tag111 { get; set; }

            [JsonPropertyName("tag121")]
            [DisplayName("Tag 121")]
            public string Tag121 { get; set; }

            [JsonPropertyName("creditor")]
            [DisplayName("Creditor")]
            public string Creditor { get; set; }

            [JsonPropertyName("debitor")]
            [DisplayName("Debitor")]
            public string Debitor { get; set; }

            [JsonPropertyName("codeTranslation")]
            [DisplayName("Code Translation")]
            public string CodeTranslation { get; set; }

            [JsonPropertyName("txtMtTrd")]
            [DisplayName("Txt Mt Trd")]
            public string TxtMtTrd { get; set; }

            [JsonPropertyName("formatEtTypeMessage")]
            [DisplayName("Format Et Type Message")]
            public string FormatEtTypeMessage { get; set; }

            [JsonPropertyName("referenceTRNEdition")]
            [DisplayName("Reference TRN Edition")]
            public string ReferenceTRNEdition { get; set; }

            [JsonPropertyName("statutSHINEText")]
            [DisplayName("Statut SHINE Text")]
            public string StatutSHINEText { get; set; }

            [JsonPropertyName("statutOLAFText")]
            [DisplayName("Statut OLAF Text")]
            public string StatutOLAFText { get; set; }

            [JsonPropertyName("codeDeviseEdition")]
            [DisplayName("Code Devise Edition")]
            public string CodeDeviseEdition { get; set; }

            [JsonPropertyName("montantEdition")]
            [DisplayName("Montant Edition")]
            public string MontantEdition { get; set; }

            [JsonPropertyName("dateValeurEdition")]
            [DisplayName("Date Valeur Edition")]
            public string DateValeurEdition { get; set; }

            [JsonPropertyName("refFrontaleMIRText")]
            [DisplayName("Ref Frontale MIR Text")]
            public string RefFrontaleMIRText { get; set; }

            [JsonPropertyName("refFrontaleMORText")]
            [DisplayName("Ref Frontale MOR Text")]
            public string RefFrontaleMORText { get; set; }

            [JsonPropertyName("texteExportMTFe")]
            [DisplayName("Texte Export MT Fe")]
            public string TexteExportMTFe { get; set; }

            [JsonPropertyName("multiFormats")]
            [DisplayName("Multi Formats")]
            public string MultiFormats { get; set; }
        }

        public class Search
        {
            [JsonPropertyName("liste")]
            [DisplayName("Liste")]
            public List<Message> Liste { get; set; }

            [JsonPropertyName("nombreTotalDeReponses")]
            [DisplayName("Nombre Total De Reponses")]
            public string NombreTotalDeReponses { get; set; }

            [JsonPropertyName("nombreDeReponsesSIBES")]
            [DisplayName("Nombre De Reponses SIBES")]
            public string NombreDeReponsesSIBES { get; set; }
        }
    }

    public static class PayloadGenerator
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions()
        {
            // Required to keep null values in the output
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            // Preserve the property names you write in the dictionary (no naming policy)
            PropertyNamingPolicy = null,
            WriteIndented = false
        };

        public static string GeneratePayload(Dictionary<string, object?> parameters)
            => JsonSerializer.Serialize(parameters, Options);
    }
}