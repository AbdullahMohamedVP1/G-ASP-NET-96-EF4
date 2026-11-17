using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NationalBank.Data;
using NationalBank.Enums;
using NationalBank.Models;

Console.OutputEncoding = Encoding.UTF8;

// the context is disposed automatically when the program ends
using var db = new BankDbContext();

bool running = true;
while (running)
{
    Console.Clear();
    ShowMenu();
    string? choice = Console.ReadLine()?.Trim();

    try
    {
        switch (choice)
        {
            case "1": AddCustomer(db); break;
            case "2": OpenAccount(db); break;
            case "3": UpdateAccountStatus(db); break;
            case "4": RemoveAccountFromCustomer(db); break;
            case "5": ListCustomers(db); break;
            case "0":
                running = false;
                continue;
            default:
                Error("Invalid choice. Please enter a number from the menu.");
                break;
        }
    }
    catch (DbUpdateException ex)
    {
        Error($"Database error: {ex.InnerException?.Message ?? ex.Message}");
    }
    catch (Exception ex)
    {
        Error($"Unexpected error: {ex.Message}");
    }

    Pause();
}

Console.WriteLine("Goodbye!");


//   menu  
static void ShowMenu()
{
    Console.WriteLine("========================================");
    Console.WriteLine("    National Bank — Management");
    Console.WriteLine("========================================");
    Console.WriteLine("  1) Add a new Customer");
    Console.WriteLine("  2) Open a new Account for a Customer");
    Console.WriteLine("  3) Update Account Status (Active / Closed)");
    Console.WriteLine("  4) Remove an Account from a Customer");
    Console.WriteLine("  5) List all Customers (with accounts)");
    Console.WriteLine("  0) Exit");
    Console.WriteLine("    ");
    Console.Write("  Enter choice: ");
}


//   1) add customer  
static void AddCustomer(BankDbContext db)
{
    Console.WriteLine("--- Add New Customer ---");

    string name = ReadText("Full Name      : ", 3, 100);
    string nationalId = ReadNationalId(db);
    DateTime birthDate = ReadBirthDate();
    string email = ReadEmail();
    string phone = ReadPhone();
    string address = ReadText("Address        : ", 5, 200);
    CustomerType customerType = ReadEnum<CustomerType>("Customer Type:");

    Customer customer = new Customer();
    customer.FullName = name;
    customer.NationalId = nationalId;
    customer.DateOfBirth = birthDate;
    customer.Email = email;
    customer.PhoneNumber = phone;
    customer.Address = address;
    customer.CustomerType = customerType;

    db.Customers.Add(customer);
    db.SaveChanges();

    Console.WriteLine();
    Success($"Customer created successfully. CustomerId = {customer.Id}");
}


//   2) open account  
static void OpenAccount(BankDbContext db)
{
    Console.WriteLine("--- Open New Account ---");

    string accountNumber = ReadAccountNumber("Account Number : ");
    AccountType accountType = ReadEnum<AccountType>("Account Type:");
    string branchCode = ReadText("Branch Code    : ", 1, 10);
    int customerId = ReadInt("Customer Id    : ");
    OwnershipType role = ReadEnum<OwnershipType>("Ownership Role:");

    Info($"Validating branch '{branchCode}' and customer #{customerId}...");

    // check the branch
    var branch = db.Branches.FirstOrDefault(b => b.Code == branchCode);
    if (branch == null)
    {
        Error($"Branch '{branchCode}' does not exist.");
        return;
    }

    // check the customer
    var customer = db.Customers.Find(customerId);
    if (customer == null)
    {
        Error($"Customer #{customerId} does not exist.");
        return;
    }

    // see if the account is already there
    var account = db.Accounts
        .Include(a => a.CustomerAccounts)
        .FirstOrDefault(a => a.AccountNumber == accountNumber);

    if (account == null)
    {
        // new account so the first owner has to be Primary
        if (role != OwnershipType.Primary)
        {
            Error("A new account must be opened with a Primary owner first.");
            return;
        }

        account = new Account();
        account.AccountNumber = accountNumber;
        account.AccountType = accountType;
        account.BranchCode = branch.Code;
        account.CurrentBalance = 0m;
        account.OpeningDate = DateTime.Now;
        db.Accounts.Add(account);
    }
    else
    {
        // account exists so this is a joint account, we just add another owner
        if (account.BranchCode != branch.Code || account.AccountType != accountType)
        {
            Error($"Account '{accountNumber}' already exists as {account.AccountType} at branch '{account.BranchCode}'.");
            return;
        }

        if (account.CustomerAccounts.Any(ca => ca.CustomerId == customerId))
        {
            Error($"Customer #{customerId} is already linked to account '{accountNumber}'.");
            return;
        }

        if (role == OwnershipType.Primary &&
            account.CustomerAccounts.Any(ca => ca.OwnershipType == OwnershipType.Primary))
        {
            Error("This account already has a Primary owner. Use CoHolder instead.");
            return;
        }
    }

    CustomerAccount link = new CustomerAccount();
    link.CustomerId = customerId;
    link.AccountNumber = account.AccountNumber;
    link.OwnershipType = role;
    link.OwnershipStartDate = DateTime.Now;
    link.AccountStatus = AccountStatus.Active;
    db.CustomerAccounts.Add(link);

    db.SaveChanges();
    Success($"Account '{account.AccountNumber}' created and linked to customer {customerId} as {role} owner.");
}


//   3) update status  
static void UpdateAccountStatus(BankDbContext db)
{
    Console.WriteLine("--- Update Account Status ---");

    string accountNumber = ReadAccountNumber("Account Number : ");
    int customerId = ReadInt("Customer Id    : ");

    var link = db.CustomerAccounts
        .FirstOrDefault(ca => ca.AccountNumber == accountNumber && ca.CustomerId == customerId);

    if (link == null)
    {
        Error("No link found between this account and this customer.");
        return;
    }

    AccountStatus newStatus = ReadEnum<AccountStatus>("New Status:");

    if (link.AccountStatus == newStatus)
    {
        Info($"Status is already {newStatus}. Nothing to change.");
        return;
    }

    link.AccountStatus = newStatus;
    db.SaveChanges();
    Success($"Status updated to {newStatus}.");
}


//   4) remove account from customer  
static void RemoveAccountFromCustomer(BankDbContext db)
{
    Console.WriteLine("--- Remove Account From Customer ---");

    string accountNumber = ReadAccountNumber("Account Number : ");
    int customerId = ReadInt("Customer Id    : ");

    var link = db.CustomerAccounts
        .FirstOrDefault(ca => ca.AccountNumber == accountNumber && ca.CustomerId == customerId);

    if (link == null)
    {
        Error("No link found between this account and this customer.");
        return;
    }

    string number = link.AccountNumber;

    db.CustomerAccounts.Remove(link);
    db.SaveChanges();

    Console.WriteLine();
    Success("  Ownership link deleted.");

    // if no one owns the account anymore we try to delete it
    bool stillHasOwners = db.CustomerAccounts.Any(ca => ca.AccountNumber == number);
    if (stillHasOwners)
        return;

    bool hasTransactions = db.Transactions.Any(t => t.AccountNumber == number);
    if (hasTransactions)
    {
        Info($"      That was the last owner, but account '{number}' has transaction history, so it was kept.");
    }
    else
    {
        var account = db.Accounts.Find(number);
        if (account != null)
        {
            db.Accounts.Remove(account);
            db.SaveChanges();
        }
        Info($"      That was the last owner — account '{number}' was also removed.");
    }
}


//   5) list customers  
static void ListCustomers(BankDbContext db)
{
    Console.WriteLine("--- All Customers ---");
    Console.WriteLine();

    // eager loading: accounts and the branch of each account
    var customers = db.Customers
        .AsNoTracking()
        .Include(c => c.CustomerAccounts)
            .ThenInclude(ca => ca.Account)
                .ThenInclude(a => a.Branch)
        .OrderBy(c => c.Id)
        .ToList();

    if (customers.Count == 0)
    {
        Console.WriteLine("  (no customers yet)");
        return;
    }

    foreach (var c in customers)
    {
        Console.WriteLine($"  #{c.Id} {c.FullName} ({c.CustomerType})");

        if (c.CustomerAccounts.Count == 0)
        {
            Console.WriteLine("      (no accounts)");
            continue;
        }

        foreach (var ca in c.CustomerAccounts)
        {
            var a = ca.Account;
            Console.WriteLine(
                $"      {a.AccountNumber,-10} {a.AccountType,-9} Balance: {a.CurrentBalance,12:N2}   " +
                $"{ca.OwnershipType,-8} {ca.AccountStatus,-7} @ {a.Branch.Name}");
        }
    }
}


//   input methods (keep asking until the input is valid)  
static string ReadText(string prompt, int min, int max)
{
    while (true)
    {
        Console.Write(prompt);
        string text = (Console.ReadLine() ?? "").Trim();

        if (text.Length < min || text.Length > max)
        {
            Error($"Value is required and must be between {min} and {max} characters.");
            continue;
        }

        return text;
    }
}

static int ReadInt(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        string? text = Console.ReadLine()?.Trim();

        if (int.TryParse(text, out int number) && number > 0)
            return number;

        Error("Please enter a valid positive number.");
    }
}

static string ReadAccountNumber(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        string text = (Console.ReadLine() ?? "").Trim();

        if (Regex.IsMatch(text, @"^[A-Za-z0-9\-]{4,20}$"))
            return text;

        Error("Account number must be 4-20 characters (letters, digits, or '-').");
    }
}

static string ReadNationalId(BankDbContext db)
{
    while (true)
    {
        Console.Write("National ID    : ");
        string text = (Console.ReadLine() ?? "").Trim();

        if (!Regex.IsMatch(text, @"^\d{14}$"))
        {
            Error("National ID must be exactly 14 digits.");
            continue;
        }

        if (db.Customers.Any(c => c.NationalId == text))
        {
            Error("A customer with this National ID already exists.");
            continue;
        }

        return text;
    }
}

static DateTime ReadBirthDate()
{
    while (true)
    {
        Console.Write("Date of Birth  : (yyyy-MM-dd) ");
        string text = (Console.ReadLine() ?? "").Trim();

        bool ok = DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime date);

        if (!ok)
        {
            Error("Invalid date. Use the format yyyy-MM-dd (e.g. 2000-01-25).");
            continue;
        }

        // must be 18 or older
        if (date < new DateTime(1900, 1, 1) || date > DateTime.Today.AddYears(-18))
        {
            Error("Customer must be at least 18 years old (and born after 1900).");
            continue;
        }

        return date;
    }
}

static string ReadEmail()
{
    while (true)
    {
        Console.Write("Email          : ");
        string text = (Console.ReadLine() ?? "").Trim();

        try
        {
            MailAddress mail = new MailAddress(text);
            if (mail.Address == text && text.Length <= 150 && text.Contains('.'))
                return text;
        }
        catch (FormatException)
        {
            // not a valid email, the error below will show
        }

        Error("Invalid email address.");
    }
}

static string ReadPhone()
{
    while (true)
    {
        Console.Write("Phone          : ");
        string text = (Console.ReadLine() ?? "").Trim();

        if (Regex.IsMatch(text, @"^\+?\d{8,15}$"))
            return text;

        Error("Phone must contain 8-15 digits (optional leading +).");
    }
}

// shows the enum values as a numbered list and returns the one the user picked
static T ReadEnum<T>(string title) where T : struct, Enum
{
    var values = Enum.GetValues<T>();

    while (true)
    {
        Console.WriteLine(title);
        for (int i = 0; i < values.Length; i++)
        {
            Console.WriteLine($"    {i + 1}) {values[i]}");
        }
        Console.Write("  Choice: ");

        string? text = Console.ReadLine()?.Trim();
        if (int.TryParse(text, out int number) && number >= 1 && number <= values.Length)
            return values[number - 1];

        Error($"Please choose a number between 1 and {values.Length}.");
    }
}


//   console helpers  
static void Success(string message)
{
    Print(message, ConsoleColor.Green);
}

static void Error(string message)
{
    Print(message, ConsoleColor.Red);
}

static void Info(string message)
{
    Print(message, ConsoleColor.Cyan);
}

static void Print(string message, ConsoleColor color)
{
    ConsoleColor oldColor = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = oldColor;
}

static void Pause()
{
    Console.WriteLine();
    Console.WriteLine("Press any key to return to the menu...");
    Console.ReadKey(true);
}
