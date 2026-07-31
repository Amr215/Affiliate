# Amazon Tracker - Oxylabs Integration Implementation

## Overview
This implementation adds automatic Amazon product scraping every 10 minutes using the Oxylabs API. The system fetches product data and stores comprehensive information including pricing, ratings, and product attributes in the database.

## Components Created/Modified

### 1. **Models** (Updated)

#### Product.cs
- Added `Asin` - Amazon Standard Identification Number (unique product identifier)
- Added `LowestPrice`, `HighestPrice` - Track price ranges
- Added `Rating`, `ReviewsCount` - Customer feedback metrics
- Added `IsPrime`, `IsSponsored`, `IsBestSeller` - Product flags
- Added `Currency`, `Manufacturer`, `ImageUrl` - Product details
- Added `Position` - Search result ranking
- Added `ShippingInformation` - Shipping details

#### PriceHistory.cs
- Added `PriceUpper` - Upper price boundary
- Added `Rating`, `ReviewsCount`, `IsPrime`, `IsSponsored`, `IsBestSeller` - Historical tracking
- Added `ShippingInformation` - Track shipping changes over time

### 2. **DTOs** (New)

#### Models/Dtos/OxylabsApiResponse.cs
Created strongly-typed classes to deserialize Oxylabs API responses:
- `OxylabsApiResponse` - Root response container
- `JobInfo` - Job metadata
- `ResultData` - Result container
- `ContentData` - Content wrapper
- `ProductResults` - Results container
- `OrganicProduct` - Individual product data from search results

### 3. **Services** (New)

#### Services/AmazonScraperService.cs
Main service that:
- Calls the Oxylabs API with hardcoded credentials
- Sends request with parameters:
  - **Source**: amazon_search
  - **Domain**: eg (Egypt)
  - **Query**: "book"
  - **Locale**: en-AE
  - **Sorting**: price_low_to_high
  - **Location**: Cairo
- Deserializes API response
- Extracts organic products from results
- **Updates existing products** by ASIN if they already exist
- **Creates new products** if they don't exist
- **Tracks lowest price** across all historical checks
- **Records price history** for each check with all product attributes

#### Services/AmazonScraperBackgroundService.cs
Background service that:
- Inherits from `BackgroundService`
- Runs scraper **immediately on app startup**
- Runs scraper **every 10 minutes** using a Timer
- Uses dependency injection to access the scraper service
- Includes proper logging

### 4. **Program.cs** (Updated)
Registered services:
```csharp
builder.Services.AddScoped<IAmazonScraperService, AmazonScraperService>();
builder.Services.AddHostedService<AmazonScraperBackgroundService>();
```

### 5. **Database Migration** (New)
Created migration file `20260718000000_AddOxylabsProductFields.cs` that adds:

**Products table**:
- Asin (nvarchar)
- Currency (nvarchar)
- ImageUrl (nvarchar)
- IsBestSeller (bit)
- IsPrime (bit)
- IsSponsored (bit)
- Manufacturer (nvarchar)
- Position (int)
- Rating (float)
- ReviewsCount (int)
- ShippingInformation (nvarchar)
- HighestPrice (decimal)

**PriceHistories table**:
- PriceUpper (decimal)
- IsBestSeller (bit nullable)
- IsPrime (bit nullable)
- IsSponsored (bit nullable)
- Rating (float nullable)
- ReviewsCount (int nullable)
- ShippingInformation (nvarchar)

## How It Works

1. **Startup**: Application starts and runs initial scrape
2. **API Call**: Every 10 minutes, `AmazonScraperService` calls Oxylabs with:
   ```
   POST https://realtime.oxylabs.io/v1/queries
   Auth: Basic (Base64 encoded username:password)
   ```

3. **Data Processing**:
   - For each product in response:
     - If ASIN exists: Update product attributes
     - If ASIN is new: Create product entry
     - Always: Add price history record with snapshot of all attributes

4. **Database State**:
   - **Products table**: Always has latest data (merged updates)
   - **PriceHistories table**: Complete audit trail of all price checks with all attributes at that time

## Configuration

Oxylabs credentials are hardcoded in `AmazonScraperService`:
```csharp
private const string OxylabsUsername = "AmrAmin_QTiRh";
private const string OxylabsPassword = "7cJU=ilkHUq8VEa";
```

To make this more secure, consider moving to `appsettings.json`:
```json
{
  "Oxylabs": {
    "Username": "AmrAmin_QTiRh",
    "Password": "7cJU=ilkHUq8VEa",
    "ApiUrl": "https://realtime.oxylabs.io/v1/queries"
  }
}
```

## API Request Parameters

The service sends this payload every 10 minutes:
```json
{
    "source": "amazon_search",
    "domain": "eg",
    "query": "book",
    "locale": "en-AE",
    "start_page": 1,
    "pages": 1,
    "parse": true,
    "context": [
        { "key": "sort_by", "value": "price_low_to_high" },
        { "key": "geo_location", "value": "Cairo" }
    ]
}
```

## Features

? Automatic 10-minute interval scraping
? Runs on app startup
? Updates existing products (by ASIN)
? Creates new products
? Tracks price history with full product snapshot
? Handles errors gracefully with logging
? Uses Basic Authentication for Oxylabs API
? Deserializes complex nested JSON responses
? Calculates lowest price across all history
? Stores product attributes: ratings, reviews, prime status, etc.

## Database Schema

### Products
| Column | Type | Purpose |
|--------|------|---------|
| Id | int | Primary key |
| Asin | nvarchar | Unique product identifier |
| Name | nvarchar | Product title |
| Url | nvarchar | Amazon link |
| CurrentPrice | decimal | Latest price |
| LowestPrice | decimal | Minimum price seen |
| HighestPrice | decimal | Maximum price seen |
| Rating | float | Average customer rating |
| ReviewsCount | int | Number of reviews |
| IsPrime | bit | Prime eligibility |
| IsSponsored | bit | Sponsored product flag |
| IsBestSeller | bit | Best seller badge |
| Currency | nvarchar | Price currency |
| ImageUrl | nvarchar | Product image |
| ShippingInformation | nvarchar | Shipping details |
| CreatedAt | datetime | Record creation time |
| LastCheckedAt | datetime | Last API check time |

### PriceHistories
| Column | Type | Purpose |
|--------|------|---------|
| Id | int | Primary key |
| ProductId | int | Foreign key to Products |
| Price | decimal | Price at check time |
| PriceUpper | decimal | Upper price range |
| Rating | float | Rating at check time |
| ReviewsCount | int | Reviews count at check time |
| IsPrime | bit | Prime status at check time |
| IsSponsored | bit | Sponsored status at check time |
| IsBestSeller | bit | Best seller status at check time |
| ShippingInformation | nvarchar | Shipping info at check time |
| CheckedAt | datetime | When this check occurred |
| Status | nvarchar | Availability status |

## Logging

The service logs important events:
- Service start/stop
- API request success/failure
- Product count found
- Individual product errors
- Overall success count

## Next Steps (Optional Enhancements)

1. Move credentials to `appsettings.json` for security
2. Add configuration for query parameters (domain, query, locale)
3. Add error retry logic
4. Add metrics/monitoring
5. Add endpoints to view scraped data
6. Add email alerts for price drops
7. Paginate through multiple pages of results
8. Add support for multiple search queries

