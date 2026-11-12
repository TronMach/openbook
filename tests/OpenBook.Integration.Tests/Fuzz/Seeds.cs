namespace OpenBook.Integration.Tests.Fuzz;

/// <summary>Valid samples of each format the fuzzer damages: most from the readers' own tests.</summary>
internal static class Seeds
{
    public const string Iif =
        "!ACCNT\tNAME\tACCNTTYPE\tDESC\tACCNUM\r\n" +
        "ACCNT\tBusiness Checking\tBANK\tMain account\t1010\r\n" +
        "ACCNT\tConsulting Income\tINC\t\t4010\r\n" +
        "ACCNT\tOffice\tEXP\t\t\r\n" +
        "ACCNT\tOffice:Supplies\tEXP\t\t\r\n" +
        "ACCNT\tEstimates\tNONPOSTING\t\t\r\n" +
        "!CUST\tNAME\tCOMPANYNAME\tEMAIL\tBADDR1\tBADDR2\r\n" +
        "CUST\tAcme Ltd\tAcme Limited\tap@acme.test\t1 Main St\tSpringfield\r\n" +
        "!VEND\tNAME\tADDR1\t1099\r\n" +
        "VEND\tJane Contractor\t\tY\r\n" +
        "!INVITEM\tNAME\tINVITEMTYPE\tACCNT\tPRICE\r\n" +
        "INVITEM\tConsulting\tSERV\tConsulting Income\t100\r\n" +
        "!TRNS\tTRNSTYPE\tDATE\tACCNT\tNAME\tAMOUNT\tDOCNUM\tMEMO\r\n" +
        "!SPL\tTRNSTYPE\tDATE\tACCNT\tNAME\tAMOUNT\tDOCNUM\tMEMO\r\n" +
        "!ENDTRNS\r\n" +
        "TRNS\tGENERAL JOURNAL\t03/02/2026\tBusiness Checking\t\t250.00\t7\tOpening\r\n" +
        "SPL\tGENERAL JOURNAL\t03/02/2026\tConsulting Income\tAcme Ltd\t-250.00\t7\t\r\n" +
        "ENDTRNS\r\n";

    public const string Qbxml = """
        <?xml version="1.0" ?>
        <?qbxml version="13.0"?>
        <QBXML><QBXMLMsgsRs>
          <CompanyQueryRs statusCode="0"><CompanyRet><CompanyName>Rock Castle</CompanyName><LegalCompanyName>Rock Castle LLC</LegalCompanyName><EIN>12-3456789</EIN></CompanyRet></CompanyQueryRs>
          <AccountQueryRs statusCode="0">
            <AccountRet><Name>Checking</Name><FullName>Checking</FullName><Sublevel>0</Sublevel><AccountType>Bank</AccountType><AccountNumber>1000</AccountNumber></AccountRet>
            <AccountRet><Name>Consulting</Name><FullName>Consulting</FullName><Sublevel>0</Sublevel><AccountType>Income</AccountType><TaxLineInfoRet><TaxLineID>1</TaxLineID><TaxLineName>Sch C: Gross receipts or sales</TaxLineName></TaxLineInfoRet></AccountRet>
          </AccountQueryRs>
          <CustomerQueryRs statusCode="0">
            <CustomerRet><Name>Acme</Name><FullName>Acme</FullName><Sublevel>0</Sublevel><CompanyName>Acme Inc</CompanyName><Email>ap@acme.test</Email>
              <BillAddress><Addr1>1 Main St</Addr1><City>Springfield</City><State>IL</State><PostalCode>62701</PostalCode></BillAddress></CustomerRet>
            <CustomerRet><Name>Project X</Name><FullName>Acme:Project X</FullName><Sublevel>1</Sublevel></CustomerRet>
          </CustomerQueryRs>
          <VendorQueryRs statusCode="0"><VendorRet><Name>Paper Co</Name><IsVendorEligibleFor1099>true</IsVendorEligibleFor1099></VendorRet></VendorQueryRs>
          <ItemServiceQueryRs statusCode="0"><ItemServiceRet><Name>Hourly</Name><FullName>Hourly</FullName>
            <SalesOrPurchase><Desc>Consulting hour</Desc><Price>150.00</Price><AccountRef><FullName>Consulting</FullName></AccountRef></SalesOrPurchase></ItemServiceRet></ItemServiceQueryRs>
          <JournalEntryQueryRs statusCode="0"><JournalEntryRet><TxnDate>2026-02-01</TxnDate><RefNumber>JE-7</RefNumber><Memo>Opening cash</Memo>
            <JournalDebitLine><AccountRef><FullName>Checking</FullName></AccountRef><Amount>500.00</Amount></JournalDebitLine>
            <JournalCreditLine><AccountRef><FullName>Consulting</FullName></AccountRef><Amount>500.00</Amount></JournalCreditLine>
          </JournalEntryRet></JournalEntryQueryRs>
          <GeneralDetailReportQueryRs statusCode="0"><ReportRet><ReportTitle>Journal</ReportTitle>
            <ColDesc colID="1"><ColTitle titleRow="1" value="Trans #"/><ColType>TxnNumber</ColType></ColDesc>
            <ColDesc colID="2"><ColTitle titleRow="1" value="Date"/><ColType>Date</ColType></ColDesc>
            <ColDesc colID="3"><ColTitle titleRow="1" value="Account"/><ColType>Account</ColType></ColDesc>
            <ColDesc colID="4"><ColTitle titleRow="1" value="Debit"/><ColType>Debit</ColType></ColDesc>
            <ColDesc colID="5"><ColTitle titleRow="1" value="Credit"/><ColType>Credit</ColType></ColDesc>
            <ReportData>
              <DataRow rowNumber="1"><ColData colID="1" value="12"/><ColData colID="2" value="2026-03-01"/><ColData colID="3" value="1000 · Checking"/><ColData colID="4" value="75.00"/></DataRow>
              <DataRow rowNumber="2"><ColData colID="3" value="Consulting"/><ColData colID="5" value="75.00"/></DataRow>
              <SubtotalRow rowNumber="3"><ColData colID="4" value="75.00"/><ColData colID="5" value="75.00"/></SubtotalRow>
            </ReportData></ReportRet></GeneralDetailReportQueryRs>
        </QBXMLMsgsRs></QBXML>
        """;

    public const string QuickBooksOnline = """
        [
          {"QueryResponse": {"CompanyInfo": [{"CompanyName": "Bo's Bakery", "LegalName": "Bo LLC", "Country": "US"}]}},
          {"QueryResponse": {"Account": [
            {"Id": "35", "Name": "Checking", "FullyQualifiedName": "Checking", "AccountType": "Bank", "AcctNum": "1000"},
            {"Id": "79", "Name": "Sales", "FullyQualifiedName": "Sales", "AccountType": "Income"},
            {"Id": "80", "Name": "Online", "FullyQualifiedName": "Sales:Online", "AccountType": "Income"}
          ]}, "time": "2026-03-01T10:00:00-08:00"},
          {"QueryResponse": {"Customer": [{"DisplayName": "Bo's Bakery", "PrimaryEmailAddr": {"Address": "bo@bakery.test"}}]}},
          {"QueryResponse": {"JournalEntry": [{"TxnDate": "2026-03-05", "DocNumber": "12", "PrivateNote": "Web sales",
            "Line": [
              {"Amount": 80.0, "Description": "Deposit", "DetailType": "JournalEntryLineDetail", "JournalEntryLineDetail": {"PostingType": "Debit", "AccountRef": {"value": "35", "name": "Checking"}}},
              {"Amount": 80.0, "DetailType": "JournalEntryLineDetail", "JournalEntryLineDetail": {"PostingType": "Credit", "AccountRef": {"value": "80", "name": "Online"}}}
            ]}]}}
        ]
        """;

    public const string GnuCash = """
        <?xml version="1.0" encoding="utf-8" ?>
        <gnc-v2 xmlns:gnc="http://www.gnucash.org/XML/gnc" xmlns:act="http://www.gnucash.org/XML/act" xmlns:book="http://www.gnucash.org/XML/book"
                xmlns:cmdty="http://www.gnucash.org/XML/cmdty" xmlns:trn="http://www.gnucash.org/XML/trn" xmlns:split="http://www.gnucash.org/XML/split"
                xmlns:ts="http://www.gnucash.org/XML/ts" xmlns:slot="http://www.gnucash.org/XML/slot">
        <gnc:book version="2.0.0">
          <gnc:account version="2.0.0"><act:name>Root Account</act:name><act:id type="guid">root</act:id><act:type>ROOT</act:type></gnc:account>
          <gnc:account version="2.0.0"><act:name>Assets</act:name><act:id type="guid">assets</act:id><act:type>ASSET</act:type><act:parent type="guid">root</act:parent></gnc:account>
          <gnc:account version="2.0.0"><act:name>Checking</act:name><act:id type="guid">checking</act:id><act:type>BANK</act:type><act:code>1010</act:code><act:parent type="guid">assets</act:parent></gnc:account>
          <gnc:account version="2.0.0"><act:name>Expenses</act:name><act:id type="guid">expenses</act:id><act:type>EXPENSE</act:type><act:parent type="guid">root</act:parent></gnc:account>
          <gnc:account version="2.0.0"><act:name>Supplies</act:name><act:id type="guid">supplies</act:id><act:type>EXPENSE</act:type><act:parent type="guid">expenses</act:parent></gnc:account>
          <gnc:transaction version="2.0.0">
            <trn:id type="guid">t1</trn:id>
            <trn:currency><cmdty:space>CURRENCY</cmdty:space><cmdty:id>USD</cmdty:id></trn:currency>
            <trn:num>101</trn:num>
            <trn:date-posted><ts:date>2026-03-10 10:59:00 +0000</ts:date></trn:date-posted>
            <trn:description>Office supplies</trn:description>
            <trn:splits>
              <trn:split><split:id type="guid">s1</split:id><split:value>4250/100</split:value><split:account type="guid">supplies</split:account></trn:split>
              <trn:split><split:id type="guid">s2</split:id><split:value>-4250/100</split:value><split:account type="guid">checking</split:account></trn:split>
            </trn:splits>
          </gnc:transaction>
        </gnc:book>
        </gnc-v2>
        """;

    public const string XeroChart = "*Code,*Name,*Type,*Tax Code,Description\n090,Business Bank,Bank,No VAT,\n200,Sales,Revenue,20% (VAT on Income),\n610,Accounts Receivable,Current Asset,No VAT,\n";
    public const string XeroContacts = "*ContactName,EmailAddress,POAddressLine1,POCity,IsSupplier\nRed Ltd,red@test.example,1 High St,Leeds,false\nPaper Ltd,,,,true\n";
    public const string XeroJournal = "*Narration,*Date,Description,*AccountCode,*TaxRate,*Amount\nCash sales,2026-03-31,Till,090,No VAT,250.00\nCash sales,2026-03-31,Till,200,No VAT,-250.00\n";

    public const string Wave =
        "Transaction ID,Transaction Date,Account Name,Transaction Description,Transaction Line Description,Amount (One column),Debit Amount (Two Column Approach),Credit Amount (Two Column Approach),Customer,Vendor,Account Group,Account Type\n" +
        "T1,2026-04-02,Cash on Hand,Invoice payment,,120.00,120.00,,Dana Co,,Assets,Cash and Bank\n" +
        "T1,2026-04-02,Design Income,Invoice payment,Logo,-120.00,,120.00,Dana Co,,Income,Income\n" +
        "T2,2026-04-03,Software,Monthly plan,,30.00,30.00,,,Tools Inc,Expenses,Operating Expense\n" +
        "T2,2026-04-03,Cash on Hand,Monthly plan,,-30.00,,30.00,,Tools Inc,Assets,Cash and Bank\n";

    public const string FreshBooks = "Organization,First Name,Last Name,Email,Business Phone,Address,City,Province/State,Postal/Zip\nNorth Studio,Ana,Lee,ana@north.test,555-0100,9 Elm St,Portland,OR,97201\n,Sam,Ortiz,sam@test.example,,,,,\n";

    public const string SgmlOfx = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102

        <OFX>
        <SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20260331120000<LANGUAGE>ENG</SONRS></SIGNONMSGSRSV1>
        <BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STMTRS><CURDEF>USD
        <BANKACCTFROM><BANKID>011000015<ACCTID>000123456789<ACCTTYPE>CHECKING</BANKACCTFROM>
        <BANKTRANLIST><DTSTART>20260301<DTEND>20260331
        <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260302120000.000[-5:EST]<TRNAMT>-45.20<FITID>F100<NAME>AMZN MKTP US*2K4<MEMO>Office supplies</STMTTRN>
        <STMTTRN><TRNTYPE>CHECK<DTPOSTED>20260305<TRNAMT>-1200.00<FITID>F101<CHECKNUM>1001<NAME>CHECK 1001</STMTTRN>
        <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260310<TRNAMT>2500.00<FITID>F102<NAME>ACME CORP &amp; SONS</STMTTRN>
        </BANKTRANLIST>
        <LEDGERBAL><BALAMT>1254.80<DTASOF>20260331</LEDGERBAL>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1>
        </OFX>
        """;

    public const string XmlOfx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?OFX OFXHEADER="200" VERSION="220"?>
        <OFX><CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><CURDEF>USD</CURDEF>
        <CCACCTFROM><ACCTID>4111111111111111</ACCTID></CCACCTFROM>
        <BANKTRANLIST>
        <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260304</DTPOSTED><TRNAMT>-19,99</TRNAMT><FITID>C1</FITID><NAME>SPOTIFY</NAME></STMTTRN>
        <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260320</DTPOSTED><TRNAMT>500.00</TRNAMT><FITID>C2</FITID><NAME>PAYMENT THANK YOU</NAME></STMTTRN>
        </BANKTRANLIST></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>
        """;

    public const string Statement =
        "My Bank\nAccount ending 6789\n\n" +
        "Date,Description,Money out,Money in,Balance\n" +
        "03/02/2026,COFFEE SHOP,5.00,,1245.00\n" +
        "03/04/2026,\"ACME, INC PAYROLL\",,2500.00,3745.00\n" +
        "2026-03-05,Rent,\"1,200.00\",,2545.00\n" +
        "05.03.2026,Card fee,(2.50),,2542.50\n";

    public const string Amazon =
        "settlement-id\tsettlement-start-date\tsettlement-end-date\tdeposit-date\ttotal-amount\tcurrency\ttransaction-type\torder-id\tamount-type\tamount-description\tamount\tposted-date\tsku\n" +
        "111\t2026-03-01 00:00:00 UTC\t2026-03-14 00:00:00 UTC\t2026-03-16 00:00:00 UTC\t73.50\tUSD\t\t\t\t\t\t\t\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemPrice\tPrincipal\t80.00\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemPrice\tShipping\t5.00\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemPrice\tTax\t6.80\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemWithheldTax\tMarketplaceFacilitatorTax-Principal\t-6.80\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemFees\tCommission\t-12.00\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tRefund\t113-2\tItemPrice\tPrincipal\t-10.00\t2026-03-05 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tServiceFee\t\tother-transaction\tSubscription Fee\t-39.99\t2026-03-10 10:00:00 UTC\t\n";

    public const string Ebay =
        "\"Transaction report\"\n\"Start date\",\"2026-03-01\"\n\n" +
        "\"Transaction creation date\",\"Type\",\"Order number\",\"Item title\",\"Ship to province/region/state\",\"Net amount\",\"Payout date\",\"Payout ID\",\"Item subtotal\",\"Shipping and handling\",\"Seller collected tax\",\"eBay collected tax\",\"Final Value Fee - fixed\",\"Final Value Fee - variable\",\"Gross transaction amount\"\n" +
        "\"Mar 2, 2026\",\"Order\",\"01-1\",\"Blue mug\",\"CA\",\"40.20\",\"Mar 4, 2026\",\"P-9\",\"40.00\",\"5.00\",\"--\",\"3.60\",\"-0.30\",\"-4.50\",\"48.60\"\n" +
        "\"Mar 3, 2026\",\"Refund\",\"01-0\",\"Red mug\",\"NY\",\"-12.00\",\"Mar 4, 2026\",\"P-9\",\"--\",\"--\",\"--\",\"--\",\"--\",\"--\",\"-12.00\"\n" +
        "\"Mar 4, 2026\",\"Payout\",\"--\",\"--\",\"--\",\"-28.20\",\"Mar 4, 2026\",\"P-9\",\"--\",\"--\",\"--\",\"--\",\"--\",\"--\",\"--\"\n";

    public const string Etsy =
        "Date,Type,Title,Info,Currency,Amount,Fees & Taxes,Net,Tax Details\n" +
        "\"March 2, 2026\",Sale,Payment for Order #1,Order #1,USD,$25.00,--,$25.00,--\n" +
        "\"March 2, 2026\",Fee,Transaction fee: Mug,Order #1,USD,--,-$1.63,-$1.63,--\n" +
        "\"March 2, 2026\",Tax,Sales tax paid by buyer,Order #1,USD,--,-$2.00,-$2.00,--\n" +
        "\"March 9, 2026\",Deposit,\"$21.37 sent to your bank account\",,USD,--,--,-$21.37,--\n";

    public const string PayPal =
        "\"Date\",\"Time\",\"TimeZone\",\"Name\",\"Type\",\"Status\",\"Currency\",\"Gross\",\"Fee\",\"Net\",\"Transaction ID\",\"Balance Impact\"\n" +
        "\"03/02/2026\",\"10:00:00\",\"PST\",\"Pat Buyer\",\"Express Checkout Payment\",\"Completed\",\"USD\",\"50.00\",\"-1.95\",\"48.05\",\"T1\",\"Credit\"\n" +
        "\"03/03/2026\",\"10:00:00\",\"PST\",\"Pat Buyer\",\"Payment Refund\",\"Completed\",\"USD\",\"-10.00\",\"0.30\",\"-9.70\",\"T2\",\"Debit\"\n" +
        "\"03/04/2026\",\"10:00:00\",\"PST\",\"\",\"General Withdrawal\",\"Completed\",\"USD\",\"-30.00\",\"0.00\",\"-30.00\",\"T3\",\"Debit\"\n";

    public const string ShopifyPayouts =
        "Transaction Date,Type,Order,Payout Status,Payout Date,Payout ID,Amount,Fee,Net\n" +
        "2026-03-02,charge,#1001,paid,2026-03-04,778,100.00,3.20,96.80\n" +
        "2026-03-03,refund,#1000,paid,2026-03-04,778,-20.00,0.00,-20.00\n" +
        "2026-03-05,charge,#1002,paid,2026-03-07,779,50.00,1.75,48.25\n";

    public const string AmazonOrders =
        "amazon-order-id\tpurchase-date\torder-status\tproduct-name\tsku\tquantity\titem-price\titem-tax\tshipping-price\tship-state\n" +
        "113-1\t2026-03-02T10:00:00+00:00\tShipped\tBlue mug\tMUG-1\t2\t80.00\t6.80\t5.00\tWA\n" +
        "113-2\t2026-03-02T11:00:00+00:00\tCancelled\tBlue mug\tMUG-1\t1\t40.00\t0\t0\tOR\n";

    public const string EtsyOrders =
        "Sale Date,Item Name,Quantity,Price,Discount Amount,Order Shipping,Order Sales Tax,Ship State,Order ID,SKU\n" +
        "03/02/26,Blue mug,1,40.00,0,6.00,3.20,NY,900,MUG-1\n" +
        "03/02/26,Red mug,2,30.00,5.00,6.00,3.20,NY,900,MUG-2\n";

    public const string ShopifyOrders =
        "Name,Created at,Financial Status,Shipping,Taxes,Lineitem quantity,Lineitem name,Lineitem price,Lineitem sku,Shipping Province,Cancelled at\n" +
        "#1001,2026-03-02 09:00:00 -0500,paid,10.00,10.00,2,Blue mug,40.00,MUG-1,CA,\n" +
        "#1001,,,,,1,Red mug,30.00,MUG-2,,\n";

    public const string Returns =
        "return-date,order-id,sku,quantity,detailed-disposition,reason\n" +
        "2026-03-12,113-1,MUG-1,1,SELLABLE,UNWANTED_ITEM\n" +
        "2026-03-13,113-1,MUG-1,1,DAMAGED,DEFECTIVE\n";

    public const string VendorInvoice = """
        Paper Co
        12 Mill Road, Springfield IL 62701

        INVOICE
        Invoice number: INV-2026-0042
        Invoice date: March 3, 2026
        Due date: 04/02/2026
        Your PO: PO-1004

        SKU      Description              Qty   Unit price   Amount
        PP-500   Copy paper, 500 sheets   10    4.25         42.50
        TN-12    Toner cartridge          2     61.00        122.00
        Shipping                                              8.00
        Subtotal                                            172.50
        Sales tax                                            10.35
        Total due                                          $182.85
        """;

    public const string Receipt = """
        CORNER CAFE
        123 Main St
        03/05/2026 08:14
        Latte            4.50
        Muffin           3.25
        Subtotal         7.75
        Tax              0.62
        TOTAL            8.37
        VISA ****1234
        """;

    public const string ClaudeAnswer = """
        {"party": "Paper Co", "number": "INV-2026-0042", "date": "2026-03-03", "due_date": "2026-04-02", "order_reference": "PO-1004",
         "subtotal": 164.50, "tax": 10.35, "shipping": 8.00, "total": 182.85,
         "lines": [{"sku": "PP-500", "description": "Copy paper", "quantity": 10, "unit_price": 4.25, "amount": 42.50},
                   {"sku": "TN-12", "description": "Toner", "quantity": 2, "unit_price": 61.00, "amount": 122.00}]}
        """;

    public const string SimpleFin = """
        {"errors": [], "accounts": [
          {"org": {"domain": "bank.example", "name": "Example Bank"}, "id": "ACT-1", "name": "Checking", "currency": "USD",
           "balance": "1254.80", "available-balance": "1254.80", "balance-date": 1772366400,
           "transactions": [
             {"id": "T-1", "posted": 1772366400, "amount": "-45.20", "description": "AMZN MKTP", "payee": "Amazon", "memo": "Office"},
             {"id": "T-2", "posted": 0, "transacted_at": 1772452800, "amount": "2500.00", "description": "ACME PAYROLL", "pending": true}
           ]}
        ]}
        """;

    public const string Manifest = """
        {"version": "0.14.0", "url": "https://github.com/TronMach/openbook/releases/download/v0.14.0/OpenBook-Setup-0.14.0.exe",
         "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "size": 123456789, "minimumVersion": "0.9.0",
         "notes": "https://github.com/TronMach/openbook/releases/tag/v0.14.0", "published": "2026-11-12T10:00:00Z", "signature": "AAAA"}
        """;

    public const string ReportQuery = "preset=Custom&from=2026-01-01&to=2026-12-31&basis=Cash&asOf=2026-06-30&compare=Months&accountId=00000000-0000-0000-0000-000000000001&columns=date,type,number,name,memo,account,amount&groupBy=account&accounts=1000,4000&min=10&max=5000&detail=true&weeks=13&year=2026";

    public const string Address = "Acme Inc\nAttn: Accounts Payable\n1 Main St, Suite 200\nSpringfield, IL 62701-1234\nUSA";

    public const string Lots = "A-1 x 3; B-2 x 2";

    public const string Emails = "ap@acme.test, \"Pat Lee\" <pat@acme.test>; billing@north.example";
}
