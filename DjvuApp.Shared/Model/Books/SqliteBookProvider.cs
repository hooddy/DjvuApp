using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;
using DjvuApp.Djvu;
using JetBrains.Annotations;
using SQLite;

namespace DjvuApp.Model.Books
{
    public class SqliteBookProvider : IBookProvider
    {
        private sealed class SqliteBook : IBook
        {
            private string _title;
            private uint _lastOpenedPage;

            [PrimaryKey, AutoIncrement]
            public int Id { get; set; }

            public Guid Guid { get; set; }

            [MaxLength(255)]
            public string Title
            {
                get { return _title; }
                set
                {
                    if (value == _title) return;
                    _title = value;
                    OnPropertyChanged();
                }
            }

            public DateTime LastOpeningTime { get; set; }

            public uint? LastOpenedPage
            {
                get
                {
                    var isNull = _lastOpenedPage == 0;
                    return isNull ? (uint?)null : _lastOpenedPage;
                }
                set
                {
                    _lastOpenedPage = value ?? 0;
                }
            }

            public DateTime CreationTime { get; set; }

            public uint PageCount { get; set; }

            public uint Size { get; set; }

            [MaxLength(255)]
            public string Path { get; set; }

            [MaxLength(255)]
            public string ThumbnailPath { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;

            [NotifyPropertyChangedInvocator]
            private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
            }

            public bool Equals(IBook other)
            {
                return other != null && Guid == other.Guid;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as IBook);
            }

            public override int GetHashCode()
            {
                return Guid.GetHashCode();
            }
        }

        private sealed class SqliteBookmark : IBookmark
        {
            [PrimaryKey, AutoIncrement]
            public int Id { get; set; }

            public int BookId { get; set; }

            [MaxLength(255)]
            public string Title { get; set; }

            public uint PageNumber { get; set; }
        }

        private SQLiteAsyncConnection _connection;

        private async Task<SQLiteAsyncConnection> GetConnectionAsync()
        {
            if (_connection == null)
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "books.sqlite");
                _connection = new SQLiteAsyncConnection(path, true);

                await _connection.CreateTableAsync<SqliteBook>();
                await _connection.CreateTableAsync<SqliteBookmark>();
            }

            return _connection;
        }

        public async Task<IEnumerable<IBook>> GetBooksAsync()
        {
            var connection = await GetConnectionAsync();
            var items = await connection.Table<SqliteBook>().ToListAsync();
            return items;
        }

        public async Task UpdateThumbnail(IBook book)
        {
            var djvuFile = await StorageFile.GetFileFromPathAsync(book.Path);
            var document = await DjvuDocument.LoadAsync(djvuFile);
            var thumbnailFile = await SaveThumbnail(book.Guid, document);

            var sqliteBook = (SqliteBook)book;
            sqliteBook.ThumbnailPath = thumbnailFile.Path;

            var connection = await GetConnectionAsync();
            await connection.UpdateAsync(sqliteBook);
        }

        private static async Task<IStorageFile> SaveThumbnail(Guid guid, DjvuDocument document)
        {
            var page = await document.GetPageAsync(1);

            var maxWidth = 140 * DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            var aspectRatio = (double) page.Width / page.Height;

            var width = (uint) Math.Min(maxWidth, page.Width);
            var height = (uint) (width / aspectRatio);

            var bitmap = new WriteableBitmap((int) width, (int) height);
            await page.RenderRegionAsync(
                bitmap: bitmap,
                rescaledPageSize: new BitmapSize { Width = width, Height = height },
                renderRegion: new BitmapBounds { Width = width, Height = height });

            var booksFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Books", CreationCollisionOption.OpenIfExists);
            var thumbnailsFolder = await booksFolder.CreateFolderAsync("Thumbnails", CreationCollisionOption.OpenIfExists);
            var thumbnailFile = await thumbnailsFolder.CreateFileAsync($"{guid}.jpg", CreationCollisionOption.ReplaceExisting);

            using (var thumbnailStream = await thumbnailFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, thumbnailStream);

                var pixelStream = bitmap.PixelBuffer.AsStream();
                var pixels = new byte[pixelStream.Length];
                await pixelStream.ReadAsync(pixels, 0, pixels.Length);

                encoder.SetPixelData(
                    pixelFormat: BitmapPixelFormat.Bgra8,
                    alphaMode: BitmapAlphaMode.Ignore,
                    width: (uint)bitmap.PixelWidth,
                    height: (uint)bitmap.PixelHeight,
                    dpiX: 96.0,
                    dpiY: 96.0,
                    pixels: pixels);

                await encoder.FlushAsync();
            }

            return thumbnailFile;
        }

        public async Task<IBook> AddBookAsync(IStorageFile file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            var document = await DjvuDocument.LoadAsync(file);
            
            var guid = Guid.NewGuid();
            var properties = await file.GetBasicPropertiesAsync();
            var title = Path.GetFileNameWithoutExtension(file.Name);

            var booksFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Books", CreationCollisionOption.OpenIfExists);
            var djvuFolder = await booksFolder.CreateFolderAsync("Djvu", CreationCollisionOption.OpenIfExists);

            var djvuFile = await file.CopyAsync(djvuFolder, $"{guid}.djvu", NameCollisionOption.ReplaceExisting);
            var thumbnailFile = await SaveThumbnail(guid, document);

            var book = new SqliteBook
            {
                Guid = guid,
                PageCount = document.PageCount,
                CreationTime = DateTime.Now,
                Size = (uint) properties.Size,
                Title = title,
                LastOpeningTime = DateTime.Now,
                Path = djvuFile.Path,
                ThumbnailPath = thumbnailFile.Path
            };
            var connection = await GetConnectionAsync();
            await connection.InsertAsync(book);

            return book;
        }

        public async Task RemoveBookAsync(IBook book)
        {
            if (book == null)
                throw new ArgumentNullException(nameof(book));

            var connection = await GetConnectionAsync();
            await connection.DeleteAsync(book);

            var sqliteBook = (SqliteBook)book;
            var bookmarksToRemove = await connection.Table<SqliteBookmark>().Where(bookmark => bookmark.BookId == sqliteBook.Id).ToListAsync();
            foreach (var bookmark in bookmarksToRemove)
            {
                await connection.DeleteAsync(bookmark);
            }
        }

        public async Task ChangeTitleAsync(IBook book, string title)
        {
            if (book == null)
                throw new ArgumentNullException(nameof(book));
            if (string.IsNullOrWhiteSpace(title))
                throw new ArgumentException("title can't be empty", nameof(title));

            var sqliteBook = (SqliteBook) book;
            sqliteBook.Title = title;

            var connection = await GetConnectionAsync();
            await connection.UpdateAsync(sqliteBook);
        }

        public async Task<IEnumerable<IBookmark>> GetBookmarksAsync(IBook book)
        {
            if (book == null)
                throw new ArgumentNullException(nameof(book));

            var sqliteBook = (SqliteBook)book;

            var connection = await GetConnectionAsync();
            var bookmarks = connection.Table<SqliteBookmark>().Where(bookmark => bookmark.BookId == sqliteBook.Id);
            return await bookmarks.ToListAsync();
        }

        public async Task<IBookmark> CreateBookmarkAsync(IBook book, string title, uint pageNumber)
        {
            if (book == null)
                throw new ArgumentNullException(nameof(book));
            if (string.IsNullOrWhiteSpace(title))
                throw new ArgumentException("Title is empty", nameof(title));
            if (pageNumber < 1 || pageNumber > book.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageNumber));

            var sqliteBook = (SqliteBook) book;
            var bookmark = new SqliteBookmark { BookId = sqliteBook.Id, Title = title, PageNumber = pageNumber };

            var connection = await GetConnectionAsync();
            await connection.InsertAsync(bookmark);

            return bookmark;
        }

        public async Task RemoveBookmarkAsync(IBookmark bookmark)
        {
            if (bookmark == null)
                throw new ArgumentNullException(nameof(bookmark));

            var connection = await GetConnectionAsync();
            await connection.DeleteAsync(bookmark);
        }

        public async Task UpdateLastOpeningTimeAsync(IBook book)
        {
            if (book == null) 
                throw new ArgumentNullException(nameof(book));

            var sqliteBook = (SqliteBook)book;
            sqliteBook.LastOpeningTime = DateTime.Now;

            var connection = await GetConnectionAsync();
            await connection.UpdateAsync(sqliteBook);
        }

        public async Task UpdateLastOpenedPageAsync(IBook book, uint pageNumber)
        {
            if (book == null) 
                throw new ArgumentNullException(nameof(book));
            if (pageNumber < 1 || pageNumber > book.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageNumber));

            var sqliteBook = (SqliteBook)book;
            sqliteBook.LastOpenedPage = pageNumber;

            var connection = await GetConnectionAsync();
            await connection.UpdateAsync(sqliteBook);
        }
    }
}