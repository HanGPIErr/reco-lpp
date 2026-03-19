using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RecoTool.API
{
    public class SpiritGeneUser
    {
        public class Result
        {
            [JsonPropertyName("no_version_1")]
            public int NoVersion1 { get; set; }

            [JsonPropertyName("tech_out1")]
            public TechOut1 TechOut1 { get; set; }

            [JsonPropertyName("fonc_out1")]
            public FoncOut1 FoncOut1 { get; set; }
        }

        public class TechOut1
        {
            [JsonPropertyName("code_retour")]
            public int CodeRetour { get; set; }
        }

        public class FoncOut1
        {
            [JsonPropertyName("fonc_out101")]
            public FoncOut101 FoncOut101 { get; set; }
        }

        public class FoncOut101
        {
            [JsonPropertyName("g_tech")]
            public GTech GTech { get; set; }

            [JsonPropertyName("l_nom_user")]
            public string LNomUser { get; set; }

            [JsonPropertyName("l_nom_profil")]
            public string LNomProfil { get; set; }

            [JsonPropertyName("c_type_profil")]
            public string CTypeProfil { get; set; }

            [JsonPropertyName("i_id_profil")]
            public int IIdProfil { get; set; }

            [JsonPropertyName("q_list_bic")]
            public int QListBic { get; set; }

            [JsonPropertyName("g_list_bic")]
            public List<Bic> GListBic { get; set; }
        }

        public class GTech
        {
            [JsonPropertyName("c_ret")]
            public int CRet { get; set; }

            [JsonPropertyName("l_motif_erreur")]
            public string LMotifErreur { get; set; }
        }

        public class Bic
        {
            [JsonPropertyName("i_bic")]
            public string IBic { get; set; }

            [JsonPropertyName("l_lib_bic")]
            public string LLibBic { get; set; }
        }

        public class User
        {
            [JsonPropertyName("result")]
            public Result Result { get; set; }
        }

    }

    public class SpiritGeneTransactionsOutput
    {
        public class Output
        {
            [JsonPropertyName("result")]
            public Result Result { get; set; }
        }

        public class Result
        {
            [JsonPropertyName("no_version_1")]
            public int NoVersion1 { get; set; }

            [JsonPropertyName("tech_out1")]
            public TechOut1 TechOut1 { get; set; }

            [JsonPropertyName("fonc_out1")]
            public FoncOut1 FoncOut1 { get; set; }

            [JsonPropertyName("q_lignes_totales")]
            public int QLignesTotales { get; set; }

            [JsonPropertyName("q_pages_totales")]
            public int QPagesTotales { get; set; }
        }

        public class TechOut1
        {
            [JsonPropertyName("code_retour")]
            public int CodeRetour { get; set; }
        }

        public class FoncOut1
        {
            [JsonPropertyName("fonc_out101")]
            public FoncOut101 FoncOut101 { get; set; }
        }

        public class FoncOut101
        {
            [JsonPropertyName("g_tech")]
            public Gtech Gtech { get; set; }

            [JsonPropertyName("q_list_ope")]
            public int QListOpe { get; set; }

            [JsonPropertyName("g_list_ope")]
            public List<GListOpe> GListOpe { get; set; }
        }

        public class Gtech
        {
            [JsonPropertyName("c_ret")]
            public int CRet { get; set; }

            [JsonPropertyName("l_motif_erreur")]
            public string LMotifErreur { get; set; }
        }

        public class GListOpe
        {
            [JsonPropertyName("c_type_ope")]
            [DisplayName("Operation Type")]
            public string CTypeOpe { get; set; }

            [JsonPropertyName("d_rglt_ope")]
            [DisplayName("Operation Date")]
            public string DRgltOpe { get; set; }

            [JsonPropertyName("m_mt_ope")]
            [DisplayName("Operation amount")]
            public Money MOpe { get; set; }

            [JsonPropertyName("i_iban_do")]
            [DisplayName("Ordering Party IBAN")]
            public string IIbanDo { get; set; }

            [JsonPropertyName("i_bic_do")]
            [DisplayName("Ordering Party BIC")]
            public string IBicDo { get; set; }

            [JsonPropertyName("i_iban_ben")]
            [DisplayName("Beneficiary IBAN")]
            public string IIbanBen { get; set; }

            [JsonPropertyName("i_bic_ben")]
            [DisplayName("Beneficiary BIC")]
            public string IBicBen { get; set; }

            [JsonPropertyName("i_msg_id")]
            [DisplayName("Message ID")]
            public string IMsgId { get; set; }

            [JsonPropertyName("i_transid")]
            [DisplayName("Transit ID")]
            public string ITransid { get; set; }

            [JsonPropertyName("l_end_to_end_id")]
            [DisplayName("Endtoend ID")]
            public string LEndToEndId { get; set; }

            [JsonPropertyName("c_csm")]
            [DisplayName("Code CSM")]
            public string CCsm { get; set; }
        }

        public class Money
        {
            [JsonPropertyName("high")]
            public int High { get; set; }

            [JsonPropertyName("low")]
            public int Low { get; set; }

            public override string ToString()
            {
                return (Low/100m).ToString();
            }
        }
    }

    public class SpiritGeneTransactionDetailOutput
    {
        public class MTech
        {
            [JsonPropertyName("high")]
            public int High { get; set; }

            [JsonPropertyName("low")]
            public int Low { get; set; }

            public override string ToString()
            {
                return (Low / 100m).ToString();
            }
        }

        public class GDetOpe
        {
            [JsonPropertyName("d_heure_trait")]
            [DisplayName("Treatment Hour")]
            public string HeureTrait { get; set; }

            [JsonPropertyName("d_rglt_ope")]
            [DisplayName("Operation Regulation Date")]
            public string RgltOpe { get; set; }

            [JsonPropertyName("d_date_traitement")]
            [DisplayName("Treatment Date")]
            public string DateTraitement { get; set; }

            [JsonPropertyName("l_nom_ben")]
            [DisplayName("Beneficiary Name")]
            public string NomBen { get; set; }

            [JsonPropertyName("i_iban_ben")]
            [DisplayName("Beneficiary IBAN")]
            public string IbanBen { get; set; }

            [JsonPropertyName("l_lib_ope")]
            [DisplayName("Operation Label")]
            public string LibOpe { get; set; }

            [JsonPropertyName("m_mt_ope")]
            [DisplayName("Operation Amount")]
            public MTech MtOpe { get; set; }

            [JsonPropertyName("l_nom_do")]
            [DisplayName("Ordering Party Name")]
            public string NomDo { get; set; }

            [JsonPropertyName("i_iban_do")]
            [DisplayName("Ordering Party IBAN")]
            public string IbanDo { get; set; }

            [JsonPropertyName("l_end_to_end_id")]
            [DisplayName("End to End ID")]
            public string EndToEndId { get; set; }

            [JsonPropertyName("c_csm")]
            [DisplayName("CSM Code")]
            public string Csm { get; set; }

            [JsonPropertyName("c_type_ope")]
            [DisplayName("Operation Type")]
            public string TypeOpe { get; set; }

            [JsonPropertyName("i_bic_ben")]
            [DisplayName("Beneficiary BIC")]
            public string BicBen { get; set; }

            [JsonPropertyName("i_bic_do")]
            [DisplayName("Ordering Party BIC")]
            public string BicDo { get; set; }

            [JsonPropertyName("i_code_return")]
            [DisplayName("Return Code")]
            public string CodeReturn { get; set; }

            [JsonPropertyName("i_return_id")]
            [DisplayName("Return ID")]
            public string ReturnId { get; set; }

            [JsonPropertyName("i_stat_req_rsu")]
            [DisplayName("Request Status")]
            public string StatReqRsu { get; set; }

            [JsonPropertyName("i_cancelid_rro_orig")]
            [DisplayName("Original Cancellation ID")]
            public string CancelIdRroOrig { get; set; }

            [JsonPropertyName("i_case_id")]
            [DisplayName("Case ID")]
            public string CaseId { get; set; }

            [JsonPropertyName("d_date_demande")]
            [DisplayName("Request Date")]
            public string DateDemande { get; set; }

            [JsonPropertyName("c_sts_reponse")]
            [DisplayName("Response Status")]
            public string StsReponse { get; set; }
        }


        public class GTech
        {
            [JsonPropertyName("c_ret")]
            public int Ret { get; set; }

            [JsonPropertyName("l_motif_erreur")]
            public string MotifErreur { get; set; }
        }

        public class FoncOut101
        {
            [JsonPropertyName("g_det_ope")]
            public GDetOpe GDetOpe { get; set; }

            [JsonPropertyName("g_tech")]
            public GTech GTech { get; set; }
        }

        public class FoncOut1
        {
            [JsonPropertyName("fonc_out101")]
            public FoncOut101 FoncOut101 { get; set; }
        }

        public class TechOut1
        {
            [JsonPropertyName("code_retour")]
            public int CodeRetour { get; set; }
        }

        public class Result
        {
            [JsonPropertyName("no_version_1")]
            public int NoVersion1 { get; set; }

            [JsonPropertyName("tech_out1")]
            public TechOut1 TechOut1 { get; set; }

            [JsonPropertyName("fonc_out1")]
            public FoncOut1 FoncOut1 { get; set; }
        }

        public class Root
        {
            [JsonPropertyName("result")]
            public Result Result { get; set; }
        }
    }

    public class SpiritGeneTransactionDetailInput
    {
        public static string CreateTransactionBody(string TransactionId, string MsgId, string Sens = "R")
        {
            var root = new Root
            {
                NoVersion1 = 1,
                FoncIn1 = new FoncIn1
                {
                    FoncIn101 = new FoncIn101
                    {
                        TransId = TransactionId,
                        SensOpe = Sens,
                        MsgId = MsgId
                    }
                }
            };

            return JsonSerializer.Serialize(root);
        }

        public class FoncIn101
        {
            [JsonPropertyName("i_transid")]
            public string TransId { get; set; }

            [JsonPropertyName("c_sens_ope")]
            public string SensOpe { get; set; }

            [JsonPropertyName("i_msgid")]
            public string MsgId { get; set; }
        }

        public class FoncIn1
        {
            [JsonPropertyName("fonc_in101")]
            public FoncIn101 FoncIn101 { get; set; }
        }

        public class Root
        {
            [JsonPropertyName("no_version_1")]
            public int NoVersion1 { get; set; }

            [JsonPropertyName("fonc_in1")]
            public FoncIn1 FoncIn1 { get; set; }
        }

    }

    public class SpiritGeneTransactionsInput
    {
        public static string CreateTransactionBody(DateTime DateDebut, DateTime DateFin, string BIC, int MontantMin, int MontantMax, string Sens = "R")
        {
            // Création de l'objet à sérialiser
            var body = new Body
            {
                NoVersion1 = 1,
                FoncIn1 = new FoncIn1
                {
                    FoncIn101 = new FoncIn101
                    {
                        IProfilActeur = "CIB User",
                        CSensOpe = Sens,
                        DDateDebut = DateDebut.ToString("yyyy-MM-dd"),
                        DDateFin = DateFin.ToString("yyyy-MM-dd"),
                        CTypeOpe = "TOUS",
                        IBic = BIC,
                        MMontantInf = new Montant { Low = MontantMin, High = 0 },
                        MMontantSup = new Montant { Low = MontantMax, High = 0 },
                        ITransid = "",
                        IIban = "",
                        IMsgId = "",
                        ICsm = "Tous"
                    }
                }
            };

            // Sérialisation en JSON
            return JsonSerializer.Serialize(body);
        }

        public class Body
        {
            [JsonPropertyName("no_version_1")]
            public int NoVersion1 { get; set; }

            [JsonPropertyName("fonc_in1")]
            public FoncIn1 FoncIn1 { get; set; }
        }

        public class FoncIn1
        {
            [JsonPropertyName("fonc_in101")]
            public FoncIn101 FoncIn101 { get; set; }
        }

        public class FoncIn101
        {
            [JsonPropertyName("i_profil_acteur")]
            public string IProfilActeur { get; set; }

            [JsonPropertyName("c_sens_ope")]
            public string CSensOpe { get; set; }

            [JsonPropertyName("d_date_debut")]
            public string DDateDebut { get; set; }

            [JsonPropertyName("d_date_fin")]
            public string DDateFin { get; set; }

            [JsonPropertyName("c_type_ope")]
            public string CTypeOpe { get; set; }

            [JsonPropertyName("i_bic")]
            public string IBic { get; set; }

            [JsonPropertyName("m_montant_inf")]
            public Montant MMontantInf { get; set; }

            [JsonPropertyName("m_montant_sup")]
            public Montant MMontantSup { get; set; }

            [JsonPropertyName("i_transid")]
            public string ITransid { get; set; }

            [JsonPropertyName("i_iban")]
            public string IIban { get; set; }

            [JsonPropertyName("i_msg_id")]
            public string IMsgId { get; set; }

            [JsonPropertyName("i_csm")]
            public string ICsm { get; set; }
        }

        public class Montant
        {
            [JsonPropertyName("low")]
            public int Low { get; set; }

            [JsonPropertyName("high")]
            public int High { get; set; }
        }
    }
}
