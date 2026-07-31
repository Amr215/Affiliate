# Quick Start Guide - Amazon Tracker with Oxylabs

## What Was Implemented

Your application now automatically scrapes Amazon products every 10 minutes using the Oxylabs API and stores comprehensive product data in SQL Server.

## Files Added/Modified

### New Files Created:
- ? `Services/AmazonScraperService.cs` - Main scraping service
- ? `Services/AmazonScraperBackgroundService.cs` - Background job service
- ? `Models/Dtos/OxylabsApiResponse.cs` - API response DTOs
- ? `Migrations/20260718000000_AddOxylabsProductFields.cs` - Database migration

### Files Modified:
- ? `Models/Product.cs` - Added new fields
- ? `Models/PriceHistory.cs` - Added tracking fields
- ? `Program.cs` - Registered services

## How to Run

1. **Build the solution**
   ```
   dotnet build
   ```

2. **Apply migrations** (automatic on startup, or manual)
   ```
   dotnet ef database update
   ```

3. **Run the application**
   ```
   dotnet run
   ```

4. **What happens automatically**:
   - App starts ? Immediate scrape runs
   - Then every 10 minutes ? New scrape runs
   - Products are inserted/updated in database
   - Price history is recorded for analysis

## Database Changes

The migration added these columns:

### Products Table
- `Asin` - Unique product identifier
- `Currency`, `Manufacturer`, `ImageUrl`
- `Rating`, `ReviewsCount`
- `IsPrime`, `IsSponsored`, `IsBestSeller`
- `Position` - Search result rank
- `ShippingInformation`
- `HighestPrice` - Max price seen

### PriceHistories Table
- `PriceUpper` - Price range maximum
- `Rating`, `ReviewsCount` - Metrics at time of check
- `IsPrime`, `IsSponsored`, `IsBestSeller` - Status at time
- `ShippingInformation` - Info at time

## API Configuration

The service calls:
```
POST https://realtime.oxylabs.io/v1/queries
```

With credentials:
- **Username**: AmrAmin_QTiRh
- **Password**: 7cJU=ilkHUq8VEa

Search parameters:
- **Domain**: Egypt (eg)
- **Query**: "book"
- **Locale**: en-AE
- **Sort**: Price low to high
- **Location**: Cairo

## Product Data Flow

1. **API Response** ? Oxylabs API returns JSON with organic products
2. **DTO Deserialization** ? JSON converted to strongly-typed objects
3. **Upsert Logic** ? Check if product (by ASIN) exists
   - **If exists**: Update all fields, keep creation date
   - **If new**: Create product record
4. **Price History** ? Always record a snapshot of all product attributes
5. **Price Tracking** ? Auto-calculate lowest price across history

## Example Data Stored

### Product Record
```csharp
{
  "Id": 1,
  "Asin": "B0FD8PT3L7",
  "Name": "Bill Book, High-Quality, Practical Notebook...",
  "Url": "https://www.amazon.eg/...",
  "CurrentPrice": 3.76,
  "LowestPrice": 3.76,
  "Rating": 0,
  "ReviewsCount": 0,
  "IsPrime": false,
  "IsBestSeller": false,
  "Currency": "EGP",
  "Position": 1,
  "LastCheckedAt": "2026-07-18 10:30:00"
}
```

### Price History Record
```csharp
{
  "Id": 1,
  "ProductId": 1,
  "Price": 3.76,
  "Rating": 0,
  "ReviewsCount": 0,
  "IsPrime": false,
  "CheckedAt": "2026-07-18 10:30:00",
  "Status": "Available"
}
```

## Monitoring

Check application logs for:
- `"Starting Amazon product scraping at {Time}"` - Scrape started
- `"Found {Count} products in API response"` - Products found
- `"Successfully saved/updated {Count} products"` - Completion
- `"Error occurred while scraping Amazon products"` - Failures

## Security Improvements (Optional)

Move credentials to `appsettings.json`:
```json
{
  "Oxylabs": {
    "Username": "AmrAmin_QTiRh",
    "Password": "7cJU=ilkHUq8VEa"
  }
}
```

Then update `AmazonScraperService`:
```csharp
private readonly string _oxyUsername;
private readonly string _oxyPassword;

public AmazonScraperService(..., IConfiguration config, ...)
{
    _oxyUsername = config["Oxylabs:Username"];
    _oxyPassword = config["Oxylabs:Password"];
}
```

## Troubleshooting

**No data appearing?**
- Check if migrations ran: `dotnet ef database update`
- Check logs for API errors
- Verify database connection string

**API returning errors?**
- Verify Oxylabs credentials are correct
- Check if account has API quota remaining
- Ensure internet connection is stable

**Background service not running?**
- Verify `AddHostedService<AmazonScraperBackgroundService>()` in Program.cs
- Check application logs
- Ensure application is actually running

## Next Steps

1. **View the data**: Create a Razor page to display products
2. **Analyze trends**: Query price history for price changes
3. **Add alerts**: Email when products drop below threshold
4. **Expand scope**: Add more search queries
5. **Optimize**: Add caching, batch processing

