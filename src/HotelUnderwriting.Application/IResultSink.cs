namespace HotelUnderwriting.Application;

public interface IResultSink
{
    void Write(RunRecord record);
}
