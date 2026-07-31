# Database Schema - Amazon Tracker

## Overview
The database stores product information from Amazon Egypt search results and maintains a complete history of price and attribute changes.

## Tables

### Products Table
Main table storing current product information.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| Id | int | NO | Primary Key, auto-increment |
| Name | nvarchar(max) | NO | Product title from Amazon |
| Asin | nvarchar(max) | YES | Amazon Standard Identification Number |
| Url | nvarchar(max) | NO | Full Amazon product URL |
| CurrentPrice | decimal(18,2) | YES | Latest price from API |
| LowestPrice | decimal(18,2) | YES | Minimum price ever recorded |
| HighestPrice | decimal(18,2) | YES | Maximum price ever recorded |
| IsAvailable | bit | NO | Product availability status |
| Rating | float | YES | Average customer rating (0-5) |
| ReviewsCount | int | YES | Total number of customer reviews |
| IsPrime | bit | NO | Whether product has Prime shipping |
| IsSponsored | bit | NO | Whether product is sponsored/ad |
| IsBestSeller | bit | NO | Whether product has bestseller badge |
| Currency | nvarchar(max) | YES | Price currency (e.g., EGP) |
| Manufacturer | nvarchar(max) | YES | Product manufacturer name |
| ImageUrl | nvarchar(max) | YES | URL to product image |
| Position | int | YES | Position in search results (rank) |
| ShippingInformation | nvarchar(max) | YES | Shipping details and options |
| Status | nvarchar(max) | YES | Status (Available, OutOfStock, Unavailable) |
| CreatedAt | datetime2 | NO | When record was first created |
| LastCheckedAt | datetime2 | NO | When product was last updated from API |

### PriceHistories Table
Audit trail table recording every price check and product state.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| Id | int | NO | Primary Key, auto-increment |
| ProductId | int | NO | Foreign Key to Products table |
| Price | decimal(18,2) | YES | Price at time of check |
| PriceUpper | decimal(18,2) | YES | Upper price range at time of check |
| Rating | float | YES | Rating at time of check |
| ReviewsCount | int | YES | Review count at time of check |
| IsPrime | bit | YES | Prime status at time of check |
| IsSponsored | bit | YES | Sponsored status at time of check |
| IsBestSeller | bit | YES | Bestseller status at time of check |
| ShippingInformation | nvarchar(max) | YES | Shipping info at time of check |
| CheckedAt | datetime2 | NO | When this record was created |
| Status | nvarchar(max) | YES | Product status at time of check |
| ErrorMessage | nvarchar(max) | YES | Any error message from API |

## Relationships

```
Products (1) ----> (Many) PriceHistories
   Id (PK)           ProductId (FK)
```

- **One-to-Many**: Each Product has many PriceHistory records
- **Cascade Delete**: Deleting a Product deletes all its PriceHistory records
- **Foreign Key**: ProductId in PriceHistories references Products.Id

## Indexes (Auto-created by EF Core)

Recommended indexes for queries:
```sql
-- Search products by ASIN (unique identifier)
CREATE INDEX IX_Products_Asin ON Products(Asin);

-- Find recent price checks
CREATE INDEX IX_PriceHistories_CheckedAt ON PriceHistories(CheckedAt);

-- Track products by status
CREATE INDEX IX_Products_Status ON Products(Status);

-- Analyze price trends
CREATE INDEX IX_PriceHistories_ProductId_CheckedAt ON PriceHistories(ProductId, CheckedAt);
```

## Common Queries

### Get all products with latest data
```sql
SELECT * FROM Products
WHERE LastCheckedAt = DATEADD(day, -0, CAST(GETDATE() AS DATE))
ORDER BY Position;
```

### Get price history for a product
```sql
SELECT * FROM PriceHistories
WHERE ProductId = 1
ORDER BY CheckedAt DESC;
```

### Find price drops
```sql
SELECT 
    p.Id,
    p.Name,
    p.CurrentPrice,
    p.LowestPrice,
    (p.CurrentPrice - p.LowestPrice) as PriceDrop
FROM Products p
WHERE p.CurrentPrice > p.LowestPrice * 1.1
ORDER BY PriceDrop DESC;
```

### Get top-rated products
```sql
SELECT TOP 10 * FROM Products
WHERE Rating >= 4.5
AND ReviewsCount > 10
ORDER BY Rating DESC, ReviewsCount DESC;
```

### Get products with price changes in last 24 hours
```sql
SELECT DISTINCT p.* FROM Products p
INNER JOIN PriceHistories ph ON p.Id = ph.ProductId
WHERE ph.CheckedAt >= DATEADD(HOUR, -24, GETUTCDATE())
AND p.LastCheckedAt >= DATEADD(HOUR, -24, GETUTCDATE())
ORDER BY p.LastCheckedAt DESC;
```

### Compare prices across checks
```sql
WITH RankedPrices AS (
    SELECT 
        ProductId,
        Price,
        CheckedAt,
        ROW_NUMBER() OVER (PARTITION BY ProductId ORDER BY CheckedAt DESC) as RowNum
    FROM PriceHistories
    WHERE ProductId = 1
)
SELECT * FROM RankedPrices
WHERE RowNum <= 10
ORDER BY RowNum;
```

## Data Integrity

- **ASIN Uniqueness**: Products are uniquely identified by ASIN
- **Referential Integrity**: PriceHistories cannot exist without Products
- **Cascade Deletes**: Deleting Product cascades to PriceHistories
- **NOT NULL Constraints**: Core fields like Name, Url have NOT NULL constraints
- **Type Safety**: Decimal for prices, Float for ratings, Int for counts

## Storage Growth

Expected storage growth:
- **Per Product**: ~1.5 KB (minimum)
- **Per Price Check**: ~500 bytes per product
- **Monthly Growth** (288 checks/month): 
  - 50 products × 288 checks × 500 bytes = ~7.2 MB/month
  - Full product data: ~75 MB/month

## Optimization Tips

1. **Archive old data**: Move PriceHistories older than 6 months to archive table
2. **Index on ProductId**: Already created via Foreign Key
3. **Index on CheckedAt**: For time-range queries
4. **Partition by date**: For very large PriceHistories table
5. **Consider materialized views**: For common aggregations

## Migration History

- `20260718000000_AddOxylabsProductFields`: Added columns for Oxylabs API fields
  - Products: 11 new columns
  - PriceHistories: 7 new columns

